using System.Collections.Immutable;
using ImTools;
using JasperFx.MultiTenancy;

namespace Wolverine.Persistence.Durability;

// GH-5840. The plumbing an operator tool (CritterWatch) uses to take a removed tenant's database out of a
// running node: see WolverineRuntime.DeregisterTenantAsync. Internal by design (GH-4672): removing a tenant
// from a running fleet is an operator action, not an application API.

/// <summary>
///     Thrown when a tenant id that was deregistered from this process (GH-5840) is used again. Like
///     <see cref="DisabledTenantException" />, it is an <see cref="UnknownTenantIdException" />, so anything that
///     already handles an unknown tenant handles this too.
/// </summary>
public class RemovedTenantException : UnknownTenantIdException
{
    public RemovedTenantException(string tenantId) : base(
        $"Tenant '{tenantId}' was removed from this process, so its message store is no longer available. Add the tenant again through Wolverine's tenancy source, or restart the process, to use it again.",
        tenantId)
    {
    }
}

/// <summary>
///     The tenant ids deregistered from this process. Every tenanted message source checks it, so a removed tenant
///     is neither re-added by a refresh (the tenancy entry can still exist when the operator deregisters, and the
///     tenant sources never drop an entry on refresh) nor rebuilt by a stray message for it. Lives for the life of
///     the process; Wolverine's own <c>AddTenantAsync</c> readmits a tenant.
/// </summary>
internal sealed class RemovedTenants
{
    private readonly DurabilitySettings _settings;
    private ImmutableHashSet<string> _tenantIds = ImmutableHashSet<string>.Empty;

    public RemovedTenants(DurabilitySettings settings)
    {
        _settings = settings;
    }

    private string normalize(string tenantId) => _settings.TenantIdStyle.MaybeCorrectTenantId(tenantId);

    public bool IsEmpty => _tenantIds.IsEmpty;

    public void Add(string tenantId) => ImmutableInterlocked.Update(ref _tenantIds, x => x.Add(normalize(tenantId)));

    public void Readmit(string tenantId) => ImmutableInterlocked.Update(ref _tenantIds, x => x.Remove(normalize(tenantId)));

    public bool Contains(string tenantId) => !_tenantIds.IsEmpty && _tenantIds.Contains(normalize(tenantId));

    /// <summary>
    ///     Throw <see cref="RemovedTenantException" /> if this tenant was deregistered
    /// </summary>
    public void AssertNotRemoved(string tenantId)
    {
        if (Contains(tenantId)) throw new RemovedTenantException(tenantId);
    }
}

/// <summary>
///     A tenanted message source that can forget one tenant. It only forgets: the caller stops the durability agent
///     and the listeners first and disposes the stores afterward.
/// </summary>
internal interface ITenantStoreRemoval
{
    /// <summary>
    ///     Forget this tenant (the caller has already recorded it in <see cref="RemovedTenants" />). Idempotent: a
    ///     second call finds nothing.
    /// </summary>
    TenantStoreRemoval RemoveTenant(string tenantId);
}

/// <summary>
///     A store instance that left its source while another store, <paramref name="Replacement" />, still covers the
///     same database for a live tenant
/// </summary>
internal record DetachedStore(IMessageStore Store, IMessageStore Replacement);

/// <summary>
///     What a source let go of for one removed tenant. The shared-database guard lives in the split:
///     <list type="bullet">
///         <item><see cref="Orphaned" />: no live tenant uses the database any more. Its agent and listeners stop and the store is disposed.</item>
///         <item><see cref="Detached" />: this store instance goes, but another store still covers the database. The database keeps its agent and listener, handed over to the replacement.</item>
///         <item><see cref="Shared" />: the same store instance still serves another tenant. Only the removed tenant's mapping went.</item>
///     </list>
/// </summary>
internal record TenantStoreRemoval(
    IReadOnlyList<IMessageStore> Orphaned,
    IReadOnlyList<DetachedStore> Detached,
    IReadOnlyList<IMessageStore> Shared)
{
    public static TenantStoreRemoval Nothing { get; } = new([], [], []);

    /// <summary>
    ///     For the native sources that build one message store per tenant id: two tenant ids with one connection
    ///     string get a store instance each, over the same database
    /// </summary>
    public static TenantStoreRemoval FromPerTenantCache<T>(ref ImHashMap<string, T> stores, string tenantId)
        where T : class, IMessageStore
    {
        if (!stores.TryFind(tenantId, out var store)) return Nothing;

        stores = stores.Remove(tenantId);

        if (stores.Enumerate().Any(x => ReferenceEquals(x.Value, store))) return new TenantStoreRemoval([], [], [store]);

        var replacement = stores.Enumerate().Select(x => x.Value).FirstOrDefault(x => x.Uri == store.Uri);
        return replacement == null
            ? new TenantStoreRemoval([store], [], [])
            : new TenantStoreRemoval([], [new DetachedStore(store, replacement)], []);
    }
}

internal static class DatabaseCacheRemoval
{
    /// <summary>
    ///     6.x: the Marten and Polecat tenant sources each keep tenant id -> store and database identifier -> store
    ///     caches (7.0 lifts them into one base class). Forget a deregistered tenant there. The database's store goes
    ///     only when no live tenant uses it any more, and its identifier is remembered so refresh stops re-adding it.
    ///     The caller holds the source's lock.
    /// </summary>
    public static TenantStoreRemoval RemoveTenant(ref ImHashMap<string, IMessageStore> stores,
        ref ImHashMap<string, IMessageStore> databases, ref ImHashMap<string, bool> removedDatabases, string tenantId,
        RemovedTenants removed)
    {
        // A tenant resolved through FindAsync is in the tenant cache. One that only arrived through a refresh --
        // which is how the durability agent finds a tenant database -- is only in the database cache, under the
        // database identifier, which for master-table tenancy is the tenant id itself.
        if (!stores.TryFind(tenantId, out var store) && !databases.TryFind(tenantId, out store))
        {
            store = databases.Enumerate().Select(x => x.Value).FirstOrDefault(x => x.TenantIds.Contains(tenantId));
        }

        stores = stores.Remove(tenantId);

        if (store == null) return TenantStoreRemoval.Nothing;

        // Still serving another tenant: only this tenant's mapping goes
        var candidate = store;
        if (stores.Enumerate().Any(x => ReferenceEquals(x.Value, candidate))
            || store.TenantIds.Any(x => x != tenantId && !removed.Contains(x)))
        {
            return new TenantStoreRemoval([], [], [store]);
        }

        foreach (var entry in databases.Enumerate().Where(x => ReferenceEquals(x.Value, candidate)).ToArray())
        {
            databases = databases.Remove(entry.Key);
            removedDatabases = removedDatabases.AddOrUpdate(entry.Key, true);
        }

        // Another store over the same database: this one goes, the database's agent and listener carry on
        var replacement = databases.Enumerate().Select(x => x.Value).FirstOrDefault(x => x.Uri == candidate.Uri);
        return replacement == null
            ? new TenantStoreRemoval([store], [], [])
            : new TenantStoreRemoval([], [new DetachedStore(store, replacement)], []);
    }
}

/// <summary>
///     Implemented by a listener that listens to every tenant database of a queue, so a removed tenant's database
///     can be dropped from it without stopping the queue for every other tenant
/// </summary>
internal interface ITenantDatabaseListener
{
    /// <summary>
    ///     Stop listening through this store and never start listening through this store instance again. With no
    ///     <paramref name="replacement" />, the database is gone and its listener stops whatever store started it.
    ///     With one, only a listener started through this exact store stops, and listening to the database carries
    ///     on through the replacement. Returns false when nothing was stopped.
    /// </summary>
    ValueTask<bool> StopListeningToDatabaseAsync(IMessageStore store, IMessageStore? replacement);
}

/// <summary>
///     What <c>WolverineRuntime.DeregisterTenantAsync</c> did on one node, for an operator tool to show per node
/// </summary>
internal class TenantDeregistration
{
    public TenantDeregistration(string tenantId, int nodeNumber, bool isLeader)
    {
        TenantId = tenantId;
        NodeNumber = nodeNumber;
        IsLeader = isLeader;
    }

    public string TenantId { get; }
    public int NodeNumber { get; }
    public bool IsLeader { get; }

    /// <summary>
    ///     Durability agents that were running on this node and were stopped
    /// </summary>
    public List<Uri> StoppedAgents { get; } = new();

    /// <summary>
    ///     Message stores this node deregistered and disposed
    /// </summary>
    public List<Uri> DeregisteredStores { get; } = new();

    /// <summary>
    ///     Message stores kept because another live tenant still uses the same database. Only the tenant's mapping
    ///     to them was dropped.
    /// </summary>
    public List<Uri> SharedStoresKept { get; } = new();

    /// <summary>
    ///     Per tenant database listeners stopped, as "{endpoint uri} on {database}"
    /// </summary>
    public List<string> StoppedListeners { get; } = new();

    /// <summary>
    ///     Balanced mode only: whether the leader was told to drop the agents from the assignment (or this node is
    ///     the leader and did so itself)
    /// </summary>
    public bool LeaderNotified { get; set; }
}

/// <summary>
///     Envelopes still in one of a tenant's databases, so an operator tool can warn or refuse before removing it.
///     When several tenants share the database these counts are the database's, not the tenant's alone.
/// </summary>
internal record TenantPendingWork(Uri Database, int Incoming, int Outgoing, int Scheduled)
{
    public bool HasPendingWork => Incoming > 0 || Outgoing > 0 || Scheduled > 0;
}
