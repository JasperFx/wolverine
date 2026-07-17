using ImTools;
using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;

namespace Wolverine.Runtime.Agents;

public class EventStoreAgents : IAsyncDisposable
{
    private readonly IEventStore _store;
    private readonly IObserver<ShardState>[] _observers;
    private readonly SemaphoreSlim _daemonLock = new(1);
    private ImHashMap<DatabaseId, IProjectionDaemon> _daemons = ImHashMap<DatabaseId, IProjectionDaemon>.Empty;

    // The observer subscriptions taken out against each database's daemon Tracker, so they can be
    // disposed when this node stops owning that database instead of living until the whole store is
    // disposed. Absence of an entry means "not currently subscribed" - FindDaemonAsync re-subscribes a
    // cached daemon on reuse, so a database revoked and later reassigned still feeds its observers.
    private ImHashMap<DatabaseId, IDisposable[]> _observerSubscriptions = ImHashMap<DatabaseId, IDisposable[]>.Empty;

    // How many of each database's subscription agents are running on this node right now, so the 1->0
    // transition is detectable. Only mutated under _daemonLock.
    private ImHashMap<DatabaseId, int> _runningAgentCounts = ImHashMap<DatabaseId, int>.Empty;

    // Keyed by ShardName.RelativeUrl. Populated as a side effect of SupportedAgentsAsync, but also resolved
    // on demand by BuildAgentAsync (a node assigned an agent may never have enumerated its own shards). An
    // ImHashMap so the enumeration writes and the BuildAgentAsync reads are lock-free across threads. See
    // GH-3216.
    private ImHashMap<string, ShardName> _shardNames = ImHashMap<string, ShardName>.Empty;
    
    public EventStoreAgents(IEventStore store, IObserver<ShardState>[] observers)
    {
        _store = store;
        _observers = observers ?? [];
        Identity = _store.Identity;
    }
    
    public EventStoreIdentity Identity { get; }

    /// <summary>
    /// How many databases back this event store. The distribution in
    /// <see cref="EventSubscriptionAgentFamily.EvaluateAssignmentsAsync"/> keys off this: a store backed by
    /// multiple databases (static or dynamic) gets database-affine assignment — all of a database's agents
    /// kept together on one node so connection pools scale with the number of databases instead of
    /// nodes × databases — while a single-database store keeps the even blue/green spread.
    /// See JasperFx/jasperfx#486.
    /// </summary>
    public DatabaseCardinality DatabaseCardinality => _store.DatabaseCardinality;

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _observerSubscriptions.Enumerate())
        {
            foreach (var subscription in entry.Value)
            {
                subscription.SafeDispose();
            }
        }

        _observerSubscriptions = ImHashMap<DatabaseId, IDisposable[]>.Empty;

        foreach (var entry in _daemons.Enumerate())
        {
            try
            {
                await entry.Value.StopAllAsync();
                entry.Value.SafeDispose();
            }
            catch (Exception)
            {
                // TODO -- probably want to log this just in case
            }
        }

        _daemonLock.Dispose();
    }

    public async ValueTask<IProjectionDaemon> FindDaemonAsync(DatabaseId databaseId)
    {
        // Fast path only when the daemon is cached AND currently subscribed - a daemon whose
        // subscriptions were released at the 1->0 transition has to go through the lock to be
        // re-subscribed before it's handed back out.
        if (_daemons.TryFind(databaseId, out var daemon) && _observerSubscriptions.TryFind(databaseId, out _))
        {
            return daemon;
        }

        await _daemonLock.WaitAsync();
        try
        {
            // Gotta do the double lock thing
            if (!_daemons.TryFind(databaseId, out daemon))
            {
                daemon = await _store.BuildProjectionDaemonAsync(databaseId);
                _daemons = _daemons.AddOrUpdate(databaseId, daemon);
            }

            subscribeObservers(databaseId, daemon);
        }
        finally
        {
            _daemonLock.Release();
        }

        return daemon;
    }

    // Callers must hold _daemonLock.
    private void subscribeObservers(DatabaseId databaseId, IProjectionDaemon daemon)
    {
        if (_observerSubscriptions.TryFind(databaseId, out _)) return;

        var subscriptions = _observers.Select(x => daemon.Tracker.Subscribe(x)).ToArray();
        _observerSubscriptions = _observerSubscriptions.AddOrUpdate(databaseId, subscriptions);
    }

    private async ValueTask agentStartedAsync(DatabaseId databaseId)
    {
        await _daemonLock.WaitAsync();
        try
        {
            _runningAgentCounts.TryFind(databaseId, out var count);
            _runningAgentCounts = _runningAgentCounts.AddOrUpdate(databaseId, count + 1);
        }
        finally
        {
            _daemonLock.Release();
        }
    }

    // The 1->0 transition: this node just stopped the last of a database's subscription agents, so it
    // has no further interest in that database's tracker. Drop the observer subscriptions rather than
    // holding them until the store is disposed (the standing TODO in FindDaemonAsync).
    //
    // The daemon itself is deliberately left cached and undisposed. It is shared - AllDaemonsAsync()
    // hands it to Polecat's IProjectionCoordinator, and TryRebuildRegisteredProjectionAsync holds it
    // across a rebuild - so disposing it here would be a use-after-dispose for anyone mid-borrow, and
    // removing it from _daemons would strand it from the disposal sweep in DisposeAsync. Fully
    // releasing the daemon needs a quiesce hook on the JasperFx side; see GH-3376.
    private async ValueTask agentStoppedAsync(DatabaseId databaseId)
    {
        await _daemonLock.WaitAsync();
        try
        {
            _runningAgentCounts.TryFind(databaseId, out var count);

            // Clamp: a stop without a matching successful start must not drive this negative
            var remaining = Math.Max(0, count - 1);
            _runningAgentCounts = _runningAgentCounts.AddOrUpdate(databaseId, remaining);

            if (remaining > 0) return;

            if (_observerSubscriptions.TryFind(databaseId, out var subscriptions))
            {
                _observerSubscriptions = _observerSubscriptions.Remove(databaseId);
                foreach (var subscription in subscriptions)
                {
                    subscription.SafeDispose();
                }
            }
        }
        finally
        {
            _daemonLock.Release();
        }
    }

    public async ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync(CancellationToken cancellation)
    {
        var list = new List<Uri>();
        var usage = await _store.TryCreateUsage(cancellation);
        if (usage == null) return list;

        var asyncShards = usage.Subscriptions
            .Where(x => x.Lifecycle == ProjectionLifecycle.Async)
            .SelectMany(x => x.ShardNames)
            .ToArray();

        // wolverine#3280: under sharded databases + per-tenant event partitioning, multiple tenants are
        // co-located in one shard database and each draws its own event sequence, so a single store-global
        // agent per (shard, database) cannot track them — fan out one agent per (shard, tenant) instead.
        // For any other store (or a database with no tenants yet) keep the one store-global agent.
        void addAgentsForShard(DatabaseDescriptor database, DatabaseId id, ShardName shardName)
        {
            if (_store.DistributesAgentsPerTenant && database.TenantIds.Count > 0)
            {
                // Tenant ids are matched case-insensitively elsewhere in this class (see
                // TryResolveTenantDatabaseIdAsync), so dedupe the fan-out the same way, and skip blank ids —
                // a malformed usage descriptor must not yield duplicate or invalid per-tenant agent URIs.
                foreach (var tenantId in database.TenantIds
                             .Where(t => !string.IsNullOrWhiteSpace(t))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var tenantShard = shardName.ForTenant(tenantId);
                    _shardNames = _shardNames.AddOrUpdate(tenantShard.RelativeUrl, tenantShard);
                    list.Add(EventSubscriptionAgentFamily.UriFor(_store.Identity, id, tenantShard));
                }
            }
            else
            {
                _shardNames = _shardNames.AddOrUpdate(shardName.RelativeUrl, shardName);
                list.Add(EventSubscriptionAgentFamily.UriFor(_store.Identity, id, shardName));
            }
        }

        // Using this to keep from double dipping
        var databaseIds = new List<DatabaseId>();
        foreach (var database in usage.Database.Databases)
        {
            var id = new DatabaseId(database.ServerName, database.DatabaseName);
            databaseIds.Add(id);

            foreach (var shardName in asyncShards)
            {
                addAgentsForShard(database, id, shardName);
            }
        }

        if (usage.Database.MainDatabase != null)
        {
            var database = usage.Database.MainDatabase;
            var id = new DatabaseId(database.ServerName, database.DatabaseName);
            if (!databaseIds.Contains(id))
            {
                foreach (var shardName in asyncShards)
                {
                    addAgentsForShard(database, id, shardName);
                }
            }
        }

        return list;
    }

    /// <summary>
    /// Resolve the <see cref="DatabaseId" /> backing <paramref name="tenantId" /> under database-per-tenant
    /// multi-tenancy, using the store-agnostic tenant→database mapping carried on the usage descriptor.
    /// Returns null when the tenant isn't found (e.g. single-database tenancy, or an unknown tenant). See GH-3128.
    /// </summary>
    public async ValueTask<DatabaseId?> TryResolveTenantDatabaseIdAsync(string tenantId, CancellationToken cancellation)
    {
        var usage = await _store.TryCreateUsage(cancellation);
        if (usage?.Database == null)
        {
            return null;
        }

        var candidates = usage.Database.Databases.ToList();
        if (usage.Database.MainDatabase != null)
        {
            candidates.Add(usage.Database.MainDatabase);
        }

        var match = candidates.FirstOrDefault(db => db.TenantIds.Contains(tenantId, StringComparer.OrdinalIgnoreCase));
        return match == null ? null : new DatabaseId(match.ServerName, match.DatabaseName);
    }

    public async Task<EventSubscriptionAgent> BuildAgentAsync(Uri uri, DatabaseId databaseId, string shardPath)
    {
        var shardName = await ResolveShardNameAsync(shardPath, CancellationToken.None);
        if (shardName == null)
        {
            throw new ArgumentOutOfRangeException(nameof(shardPath), $"Unable to find a shard with path '{shardPath}'");
        }

        var daemon = await FindDaemonAsync(databaseId);

        return new EventSubscriptionAgent(uri, shardName, daemon)
        {
            OnStarted = () => agentStartedAsync(databaseId),
            OnStopped = () => agentStoppedAsync(databaseId)
        };
    }

    /// <summary>
    /// Resolve a shard by its <c>RelativeUrl</c>. The cache is populated lazily as a side effect of
    /// <see cref="SupportedAgentsAsync" />, which only runs on the node that evaluates assignments (the
    /// leader). A node that is *assigned* an agent may never have enumerated its own shards yet, so on a
    /// cache miss enumerate the store's registered subscriptions directly before giving up — otherwise
    /// agent start fails non-deterministically on followers / after failover. See GH-3216.
    /// </summary>
    private async ValueTask<ShardName?> ResolveShardNameAsync(string shardPath, CancellationToken cancellation)
    {
        if (_shardNames.TryFind(shardPath, out var shardName))
        {
            return shardName;
        }

        // Populate the cache from the store usage (the same source SupportedAgentsAsync reads), then retry.
        await SupportedAgentsAsync(cancellation);

        return _shardNames.TryFind(shardPath, out shardName) ? shardName : null;
    }

    /// <summary>
    /// Rebuild a REGISTERED projection addressed by its shard identity, regardless of lifecycle
    /// (Inline / Live / Async) or whether it is currently distributed as a continuous agent. Unlike
    /// <see cref="SupportedAgentsAsync" /> — which only surfaces <see cref="ProjectionLifecycle.Async" />
    /// shards as distributable agents — this matches the shard across the full registered subscription set,
    /// builds the projection daemon for the owning database, and runs the daemon's rebuild. The daemon
    /// spins up a transient rebuild agent for that projection, replays to the high-water mark, then stops
    /// it — exactly the "spin up an agent just for the rebuild, then shut it down" behavior an operator
    /// wants when rebuilding a non-running (e.g. Inline) projection. Returns false when no registered shard
    /// matches <paramref name="shardIdentity" />. See GH-3163.
    /// </summary>
    public async Task<bool> TryRebuildRegisteredProjectionAsync(string shardIdentity, string? tenantId,
        CancellationToken cancellation)
    {
        if (!ShardName.TryParse(shardIdentity, out var baseName) || baseName is null)
        {
            return false;
        }

        var usage = await _store.TryCreateUsage(cancellation);
        if (usage == null)
        {
            return false;
        }

        // Match across ALL lifecycles — an Inline/Live projection still carries its ShardNames on the
        // descriptor (the descriptor populates them before the Async-only distribution filter), so a
        // registered-but-not-distributed projection resolves here even though it has no agent URI.
        var match = usage.Subscriptions
            .SelectMany(sub => sub.ShardNames.Select(shard => (Subscription: sub, Shard: shard)))
            .FirstOrDefault(x =>
                string.Equals(x.Shard.Identity, baseName.Identity, StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.Shard.RelativeUrl, baseName.RelativeUrl, StringComparison.OrdinalIgnoreCase));

        if (match.Shard == null)
        {
            return false;
        }

        // Resolve the owning database: database-per-tenant resolves the tenant's database; otherwise the
        // main/single database (single-database per-tenant rebuilds are scoped by the tenantId argument the
        // daemon's rebuild overload understands, not by a separate database).
        DatabaseId? databaseId = null;
        if (!string.IsNullOrEmpty(tenantId))
        {
            databaseId = await TryResolveTenantDatabaseIdAsync(tenantId, cancellation);
        }

        databaseId ??= ResolveDefaultDatabaseId(usage);
        if (databaseId == null)
        {
            return false;
        }

        var daemon = await FindDaemonAsync(databaseId);

        // Rebuild scope: today an event store can only rebuild an ENTIRE projection — every shard of the
        // projection at once — so we rebuild by the projection name. The "All" shard key (the canonical
        // identity, e.g. "Trip:All") denotes exactly that whole-projection rebuild. The ONE exception is
        // per-tenant partitioning, where an individual shard IS a single tenant's partition: a non-null
        // tenantId flows to the daemon's tenant overload to rebuild just that partition, leaving the other
        // tenants untouched. For a store-global projection the tenant is a no-op. RebuildProjectionAsync
        // builds a rebuild-mode agent, replays to the high-water mark, then stops it.
        await daemon.RebuildProjectionAsync(match.Subscription.Name, tenantId, cancellation);
        return true;
    }

    private static DatabaseId? ResolveDefaultDatabaseId(EventStoreUsage usage)
    {
        if (usage.Database.MainDatabase != null)
        {
            return new DatabaseId(usage.Database.MainDatabase.ServerName, usage.Database.MainDatabase.DatabaseName);
        }

        var first = usage.Database.Databases.FirstOrDefault();
        return first == null ? null : new DatabaseId(first.ServerName, first.DatabaseName);
    }

    public async Task StartAllAsync(CancellationToken cancellationToken)
    {
        var usage = await _store.TryCreateUsage(cancellationToken);
        if (usage == null)
        {
            return;
        }

        foreach (var database in usage.Database.Databases)
        {
            var id = new DatabaseId(database.ServerName, database.DatabaseName);
            var daemon = await FindDaemonAsync(id);

            await daemon.StartAllAsync();
        }

        if (usage.Database.MainDatabase != null)
        {
            var id = new DatabaseId(usage.Database.MainDatabase.ServerName, usage.Database.MainDatabase.DatabaseName);
            var daemon = await FindDaemonAsync(id);

            await daemon.StartAllAsync();
        }
    }

    public async Task StopAllAsync(CancellationToken cancellationToken)
    {
        foreach (var kvEntry in _daemons.Enumerate())
        {
            var daemon = kvEntry.Value;
            await daemon.StopAllAsync();
        }
    }
    
    public async ValueTask<IReadOnlyList<IProjectionDaemon>> AllDaemonsAsync()
    {
        var usage = await _store.TryCreateUsage(CancellationToken.None);
        if (usage == null)
        {
            return [];
        }

        var list = new List<IProjectionDaemon>();
        
        foreach (var database in usage.Database.Databases)
        {
            var id = new DatabaseId(database.ServerName, database.DatabaseName);
            var daemon = await FindDaemonAsync(id);

            list.Add(daemon);
        }

        if (usage.Database.MainDatabase != null && usage.Database.Cardinality == DatabaseCardinality.Single)
        {
            var id = new DatabaseId(usage.Database.MainDatabase.ServerName, usage.Database.MainDatabase.DatabaseName);
            var daemon = await FindDaemonAsync(id);

            list.Add(daemon);
        }

        return list;
    }

    public IProjectionDaemon DaemonForMainDatabase()
    {
        throw new NotSupportedException("This method is not supported with the Wolverine managed projection/subscription distribution");
    }

    // Async sibling for store coordinators (e.g. Polecat's IProjectionCoordinator) whose interface
    // exposes the async signature. Same managed-distribution semantics as DaemonForMainDatabase().
    public ValueTask<IProjectionDaemon> DaemonForMainDatabaseAsync()
    {
        throw new NotSupportedException("This method is not supported with the Wolverine managed projection/subscription distribution");
    }

    public ValueTask<IProjectionDaemon> DaemonForDatabase(string databaseIdentifier)
    {
        throw new NotSupportedException("This method is not supported with the Wolverine managed projection/subscription distribution");
    }


}