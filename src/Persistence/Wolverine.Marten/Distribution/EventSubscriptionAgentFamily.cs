using ImTools;
using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.Marten.Distribution;

public class EventSubscriptionAgentFamily : IStaticAgentFamily, IEventSubscriptionAgentFamily, IAsyncDisposable
{
    public const string SchemeName = "event-subscriptions";
    private ImHashMap<string, EventStoreAgents> _stores = ImHashMap<string, EventStoreAgents>.Empty;
    private readonly IObserver<ShardState>[] _observers;
    private readonly CancellationTokenSource _cancellation = new();

    public static Uri UriFor(EventStoreIdentity storeIdentity, DatabaseId databaseId, ShardName name)
    {
        return new Uri($"{SchemeName}://{storeIdentity.Type}/{storeIdentity.Name}/{databaseId}/{name.RelativeUrl}");
    }

    /// <summary>
    /// Resolve the agent <see cref="Uri" /> for a projection/subscription shard identified by its
    /// <paramref name="shardIdentity" /> (the JasperFx <c>ShardName.Identity</c>, e.g. <c>"Trip:All"</c>)
    /// and optional <paramref name="tenantId" />, across every store this family manages.
    ///
    /// <para>Resolution consults the <em>registered</em> subscription set, so it is independent of the
    /// shard's current run state — a paused, stopped, crashed, or not-yet-started projection still
    /// resolves (this is what makes restart/rebuild of a non-running projection possible; see GH-3124).
    /// Tooling such as CritterWatch uses this instead of composing agent URIs by hand — the URI grammar
    /// lives here, not in the consumer. Returns <c>null</c> when no matching registered shard is
    /// found.</para>
    ///
    /// <para>Handles store-global shards and single-database per-tenant partitioning (where the tenant
    /// is part of the shard's <c>RelativeUrl</c>). Database-per-tenant resolution (tenant in the
    /// database segment) is matched only for the store-global case here; a per-tenant lookup in that
    /// model needs a tenant→database resolution and is handled separately.</para>
    /// </summary>
    public async ValueTask<Uri?> FindAgentUriAsync(string shardIdentity, string? tenantId,
        CancellationToken token = default)
    {
        if (!ShardName.TryParse(shardIdentity, out var baseName) || baseName is null)
        {
            return null;
        }

        var known = await SupportedAgentsAsync().ConfigureAwait(false);

        if (string.IsNullOrEmpty(tenantId))
        {
            return known.FirstOrDefault(u => MatchesShard(u, baseName.RelativeUrl));
        }

        // Single-database per-tenant partitioning: the tenant rides in the shard RelativeUrl.
        var perTenant = ShardName.Compose(baseName.Name, baseName.ShardKey, tenantId);
        return known.FirstOrDefault(u => MatchesShard(u, perTenant.RelativeUrl));
    }

    private static bool MatchesShard(Uri agentUri, string relativeUrl)
        => agentUri.AbsolutePath.TrimEnd('/').EndsWith("/" + relativeUrl, StringComparison.OrdinalIgnoreCase);
    
    public EventSubscriptionAgentFamily(IEnumerable<IEventStore> stores, IEnumerable<IObserver<ShardState>> observers)
    {
        _observers = observers.ToArray();

        foreach (var store in stores)
        {
            _stores = _stores.AddOrUpdate(store.Identity.ToString(), new EventStoreAgents(store, _observers));
        }
    }

    public string Scheme => SchemeName;
    public ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
    {
        return SupportedAgentsAsync();
    }

    public async ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
    {
        // First check that we aren't already running this!
        
        // name:type - segments[1]:Host
        // segments[2] database id
        // other segments get you the relative path
        
        var storeIdentity = $"{uri.Segments[1].Trim('/')}:{uri.Host}";
        if (_stores.TryFind(storeIdentity, out var store))
        {
            var databaseId = DatabaseId.Parse(uri.Segments[2].Trim('/'));
            var shardPath = uri.Segments.Skip(3).Join("");

            return await store.BuildAgentAsync(uri, databaseId, shardPath);
        }

        throw new AgentStartingException(uri, wolverineRuntime.Options.UniqueNodeId, new ArgumentOutOfRangeException(nameof(uri), "Unknown event projection or subscription"));
    }

    public async ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
    {
        var list = new List<Uri>();
        
        foreach (var entry in _stores.Enumerate())
        {
            var store = entry.Value;
            list.AddRange(await store.SupportedAgentsAsync(_cancellation.Token));
        }

        return list;
    }

    public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments)
    {
        assignments.DistributeEvenlyWithBlueGreenSemantics(SchemeName);
        return new ValueTask();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores.Enumerate())
        {
            await store.Value.DisposeAsync();
        }
    }

    internal EventStoreAgents FindStore(EventStoreIdentity identity)
    {
        if (_stores.TryFind(identity.ToString(), out var store))
        {
            return store;
        }

        throw new ArgumentOutOfRangeException(nameof(identity),
            $"Unknown identity {identity}, known stores are {_stores.Enumerate().Select(x => x.Key).Join(", ")}");
    }
}