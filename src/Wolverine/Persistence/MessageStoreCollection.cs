using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.Persistence;

public class MessageStoreCollection : IAgentFamily, IAsyncDisposable
{
    /// <summary>
    ///     GH-3954. A node-level marker saying "this node can run durability agents at all", published into
    ///     <see cref="Runtime.Agents.WolverineNode.Capabilities" /> at startup when the durability agent family
    ///     is registered, and consulted by the leader when it distributes <c>wolverinedb://</c> agents.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A node-level marker rather than the per-agent capability matching the blue/green and
    ///         group-affinity paths use, for two reasons. First, this family is an <see cref="IAgentFamily" />
    ///         and NOT an <see cref="IStaticAgentFamily" />, and only static families contribute to
    ///         <c>Capabilities</c> — so no <c>wolverinedb://</c> agent Uri has ever appeared there on any node,
    ///         capable or not, and per-agent matching would find zero capable nodes everywhere. Second,
    ///         <c>Capabilities</c> is a startup snapshot, while this family's agent list GROWS at runtime as
    ///         tenant databases are added; matching per agent would strand every later-added tenant database's
    ///         durability agent. Whether a node can run these agents is genuinely a per-node property —
    ///         <c>Durability.DurabilityAgentEnabled</c> turns the whole family on or off — so that is what gets
    ///         published.
    ///     </para>
    /// </remarks>
    public static readonly Uri DurabilityCapabilityUri = new("wolverine://durability-agents-enabled");

    private readonly IWolverineRuntime _runtime;
    private readonly List<MultiTenantedMessageStore> _multiTenanted = new();
    private readonly Dictionary<MultiTenantedMessageStore, ThrottledTenantRefresh> _tenantRefreshes = new();
    private ImHashMap<Uri, IMessageStore> _services = ImHashMap<Uri, IMessageStore>.Empty;
    private ImHashMap<Type, IMessageStore> _ancillaryStores = ImHashMap<Type, IMessageStore>.Empty;
    private bool _onlyOneDatabase;
    
    public MessageStoreCollection(IWolverineRuntime runtime, IEnumerable<IMessageStore> stores, IEnumerable<AncillaryMessageStore> ancillaryMessageStores) 
    {
        _runtime = runtime;

        foreach (var store in stores.Concat(ancillaryMessageStores.Select(x => x.Inner)))
        {
            if (store is MultiTenantedMessageStore multiTenanted)
            {
                _multiTenanted.Add(multiTenanted);
                _tenantRefreshes[multiTenanted] =
                    new ThrottledTenantRefresh(multiTenanted.Source,
                        () => _runtime.Options.Durability.TenantDatabaseListStaleTime);
                categorizeStore(multiTenanted.Main);
            }
            else
            {
                categorizeStore(store);
            }
        }

        foreach (var ancillaryMessageStore in ancillaryMessageStores)
        {
            if (!_services.TryFind(ancillaryMessageStore.Inner.Uri, out var store))
            {
                store = ancillaryMessageStore.Inner;
            }
            
            _ancillaryStores = _ancillaryStores.AddOrUpdate(ancillaryMessageStore.MarkerType, store);
        }

        if (_services.Count() == 1)
        {
            _onlyOneDatabase = !_multiTenanted.Any();

            // Make sure in this case that the one, single store is really
            // the "Main" store. And do it early so that this happens
            // before we get to storage building
            var messageStore = _services.Enumerate().Single().Value;
            messageStore.PromoteToMain(_runtime);
            Main = messageStore;
        }
        else
        {
            var mains = _services.Enumerate().Select(x => x.Value).Where(x => x.Role == MessageStoreRole.Main)
                .ToArray();
            if (mains.Length == 1)
            {
                if (TryFindMultiTenantedForMainStore(mains[0], out var tenanted))
                {
                    Main = tenanted;
                }
                else
                {
                    Main = mains[0];
                }
            }
        }
        
    }

    public bool HasAnyAncillaryStores()
    {
        return !_ancillaryStores.IsEmpty;
    }

    public IReadOnlyList<MultiTenantedMessageStore> MultiTenanted => _multiTenanted;

    private void categorizeStore(IMessageStore store)
    {
        if (_services.TryFind(store.Uri, out var existing))
        {
            if (store.Role == MessageStoreRole.Main && existing.Role != MessageStoreRole.Main)
            {
                _services = _services.AddOrUpdate(store.Uri, store);
            }
        }
        else
        {
            _services = _services.AddOrUpdate(store.Uri, store);
        }
    }

    private bool _hasInitialized;
    internal async ValueTask InitializeAsync()
    {
        if (_hasInitialized) return;
        _hasInitialized = true;

        foreach (var tenantedMessageStore in _multiTenanted)
        {
            await tenantedMessageStore.InitializeAsync(_runtime);
            await tenantedMessageStore.Source.RefreshAsync();
            foreach (var store in tenantedMessageStore.Source.AllActive())
            {
                categorizeStore(store);
            }
        }
        
        _onlyOneDatabase = _services.Count() == 1 && !_multiTenanted.Any();

        var mains = _services.Enumerate().Select(x => x.Value)
            .Where(x => x.Role == MessageStoreRole.Main).ToArray();

        // GH-3226: opt-in reconciliation for >1 Main store (e.g. an event-store-integrated main plus a
        // database-backed transport that also claims Main). The policy designates the store to keep as
        // Main and we demote the rest to Ancillary, rather than throwing.
        if (mains.Length > 1 && _runtime.Options.Durability.ResolveMainStoreOnConflict is { } resolveMain)
        {
            var chosen = resolveMain(mains);
            if (chosen != null && mains.Contains(chosen))
            {
                foreach (var demoted in mains.Where(x => !ReferenceEquals(x, chosen)))
                {
                    demoted.DemoteToAncillary();
                }

                mains = [chosen];
            }
        }

        if (mains.Length > 1)
        {
            throw new InvalidWolverineStorageConfigurationException(
                $"There must be exactly one message store tagged as the 'main' store, you may need to mark all but one message store as 'ancillary'. Found multiples: {mains.Select(x => x.Uri.ToString()).Join(", ")}");
        }

        if (mains.Length == 1)
        {
            if (TryFindMultiTenantedForMainStore(mains[0], out var tenanted))
            {
                Main = tenanted;
            }
            else
            {
                Main = mains[0];
            }
            
            return;
        }

        if (!_services.IsEmpty || _multiTenanted.Any())
        {
            throw new InvalidWolverineStorageConfigurationException(
                "There are valid message stores for this Wolverine system, but none has been designated as the 'Main' store");
        }

        Main = new NullMessageStore();
    }

    public IMessageStore Main { get; private set; } = new NullMessageStore();

    public DatabaseCardinality Cardinality()
    {
        if (_services.IsEmpty && !_multiTenanted.Any()) return DatabaseCardinality.None;
        
        if (_onlyOneDatabase) return DatabaseCardinality.Single;

        if (!_multiTenanted.Any()) return DatabaseCardinality.Single;

        if (_multiTenanted.Any(x => x.Source.Cardinality == DatabaseCardinality.DynamicMultiple))
            return DatabaseCardinality.DynamicMultiple;

        return DatabaseCardinality.StaticMultiple;
    }
    
    public async ValueTask<IReadOnlyList<IMessageStore>> FindAllAsync()
    {
        if (_onlyOneDatabase) return [Main];
        
        foreach (var tenantedMessageStore in _multiTenanted)
        {
            if (tenantedMessageStore.Source.Cardinality == DatabaseCardinality.DynamicMultiple)
            {
                await refreshTenantedDatabaseList(tenantedMessageStore);
            }
        }

        return new List<IMessageStore>(_services.Enumerate().Select(x => x.Value));
    }
    
    /// <summary>
    /// Find all message stores that can be cast to the type T
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public async ValueTask<IReadOnlyList<T>> FindAllAsync<T>()
    {
        foreach (var tenantedMessageStore in _multiTenanted)
        {
            if (tenantedMessageStore.Source.Cardinality == DatabaseCardinality.DynamicMultiple)
            {
                await refreshTenantedDatabaseList(tenantedMessageStore);
            }
        }

        return _services.Enumerate().Select(x => x.Value).OfType<T>().ToList();
    }

    private async ValueTask refreshTenantedDatabaseList(MultiTenantedMessageStore tenantedMessageStore)
    {
        // GH-4267. Throttled, because every FindAllAsync() lands here and some of those callers are
        // retried. Categorizing below is not throttled: it is in-memory, and a store the source
        // created for a single-tenant lookup has to reach _services either way.
        await _tenantRefreshes[tenantedMessageStore].MaybeRefreshAsync();

        foreach (var store in tenantedMessageStore.Source.AllActive())
        {
            categorizeStore(store);
        }
    }

    public async ValueTask<IMessageStore?> FindDatabaseAsync(Uri uri)
    {
        if (_services.TryFind(uri, out var service))
        {
            return service;
        }

        // Force dynamic tenanted databases to refresh
        foreach (var tenantedMessageStore in _multiTenanted)
        {
            if (tenantedMessageStore.Source.Cardinality == DatabaseCardinality.DynamicMultiple)
            {
                await refreshTenantedDatabaseList(tenantedMessageStore);
            }
        }
        
        // Try the lookup again
        if (_services.TryFind(uri, out service))
        {
            return service;
        }

        // We're going to force it to probe for missing DBs every time instead
        // of using a cached null in case it really does get added back later
        return null;
    }
 
    public async ValueTask<IReadOnlyList<IMessageStore>> FindDatabasesAsync(Uri[] uris)
    {
        if (_onlyOneDatabase) return [Main];
        
        var list = new List<IMessageStore>();
        foreach (var uri in uris)
        {
            var db = await FindDatabaseAsync(uri);
            if (db != null)
            {
                list.Add(db);
            }
        }

        return list;
    }

    public IMessageStore FindAncillaryStore(Type markerType)
    {
        if (_ancillaryStores.TryFind(markerType, out var store)) return store;

        throw new ArgumentOutOfRangeException(nameof(markerType),
            $"No known ancillary store for type {markerType.FullNameInCode()}. Known stores exist for {_ancillaryStores.Enumerate().Select(x => x.Key.FullNameInCode()).Join(", ")}");
    }

    public async Task DrainAsync()
    {
        foreach (var entry in _services.Enumerate())
        {
            await entry.Value.DrainAsync();
        }
    }

    public async Task MigrateAsync()
    {
        var stores = await FindAllAsync();
        foreach (var store in stores)
        {
            await store.Admin.MigrateAsync();
        }
    }

    /// <summary>
    ///     Verify that every store's durable storage has actually been provisioned, throwing when it has
    ///     not. The counterpart to <see cref="MigrateAsync" /> for a system that provisions its storage ahead
    ///     of startup instead of building it there, and it has to cover the same set of stores: an ancillary
    ///     store whose schema was never provisioned fails whenever something first uses it, which can be a
    ///     long way from startup.
    /// </summary>
    /// <remarks>
    ///     GH-4166: this asks each store's cheap <see cref="IMessageStoreAdmin.AssertStorageProvisionedAsync" />,
    ///     NOT the full schema diff behind <see cref="IMessageStoreAdmin.AssertStorageExistsAsync" />. Startup
    ///     under AutoCreate.None must not pay for the introspection that setting exists to avoid.
    /// </remarks>
    public async Task AssertStorageProvisionedAsync(CancellationToken token)
    {
        var stores = await FindAllAsync();
        var exceptions = new List<Exception>();

        foreach (var store in stores)
        {
            try
            {
                await store.Admin.AssertStorageProvisionedAsync(token);
            }
            catch (Exception e)
            {
                exceptions.Add(e);
            }
        }

        if (exceptions.Count != 0)
        {
            throw new AggregateException(exceptions);
        }
    }

    public string Scheme => PersistenceConstants.AgentScheme;
    public async ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
    {
        var stores = await FindAllAsync<IMessageStoreWithAgentSupport>();
        return stores.Select(x => x.Uri).ToList();
    }

    public async ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
    {
        var database = await FindDatabaseAsync(uri);
        if (database is IMessageStoreWithAgentSupport agentSupport)
        {
            return agentSupport.BuildAgent(wolverineRuntime);
        }

        throw new ArgumentOutOfRangeException(nameof(uri), $"No database with Uri {uri} supports a durability agent");
    }

    public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
    {
        return AllKnownAgentsAsync();
    }

    public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments)
    {
        // GH-3785: a shard database's durability agent follows that database's event-subscription agents,
        // so the database attracts one node's connection pool instead of two. Depends on
        // NodeAgentController evaluating this family AFTER the event-subscription family. A database with
        // no projection agents in the grid falls back to the even spread.
        var preference = DurabilityProjectionAffinity.BuildPreference(assignments);
        assignments.DistributeEvenlyWithAffinity(Scheme, preference.NodeFor, NodeCanRunDurabilityAgents);

        // The fallback is silent by design, so say out loud how much of it engaged -- see
        // DurabilityAffinityPreference. Logged only when the numbers move.
        _affinityLogger ??= _runtime.LoggerFactory.CreateLogger<MessageStoreCollection>();
        preference.ReportTo(_affinityLogger, ref _lastAffinityReport);

        WarnIfNoCapableNode(assignments, _affinityLogger, ref _warnedAboutNoCapableNode);

        return ValueTask.CompletedTask;
    }

    private ILogger? _affinityLogger;
    private (int Known, int Considered, int Matched) _lastAffinityReport;
    private bool _warnedAboutNoCapableNode;

    /// <summary>
    ///     GH-3954. Whether a node has published <see cref="DurabilityCapabilityUri" /> — i.e. it started with
    ///     <c>Durability.DurabilityAgentEnabled</c> on and registered this family.
    /// </summary>
    internal static bool NodeCanRunDurabilityAgents(AssignmentGrid.Node node)
    {
        return node.Capabilities.Contains(DurabilityCapabilityUri);
    }

    /// <summary>
    ///     GH-3954. The reported failure was silent: every queue table read zero while a nine-day backlog of
    ///     unrecovered envelopes sat in wolverine_outgoing_envelopes. Leaving the agents unassigned stops the
    ///     five-minute reassignment churn but is just as quiet on its own, so name the condition and the
    ///     setting that causes it. Latched so it is said once per transition, not every evaluation.
    /// </summary>
    internal static void WarnIfNoCapableNode(AssignmentGrid assignments, ILogger logger, ref bool alreadyWarned)
    {
        if (assignments.Nodes.Any(NodeCanRunDurabilityAgents))
        {
            alreadyWarned = false;
            return;
        }

        if (alreadyWarned)
        {
            return;
        }

        alreadyWarned = true;
        logger.LogWarning(
            "No node in this Wolverine cluster can run durability agents, so none were assigned and no outgoing envelope recovery, scheduled message processing or node reassignment will happen for any message store. Every node in the cluster started with Durability.DurabilityAgentEnabled = false. Enable it on at least one node.");
    }

    internal async Task<IAgent> StartScheduledJobProcessing(IWolverineRuntime runtime)
    {
        // First, find all unique message stores
        var stores = await FindAllAsync();
        var agents = stores.Select(x => x.StartScheduledJobs(runtime));
        return new CompositeAgent(new Uri("internal://scheduledjobs"), agents);
    }

    public bool TryFindMultiTenantedForMainStore(IMessageStore store, out MultiTenantedMessageStore multiTenanted)
    {
        multiTenanted = _multiTenanted.FirstOrDefault(x => x.Main.Uri == store.Uri)!;
        return multiTenanted != null;
    }

    public async Task ReleaseAllOwnershipAsync(int nodeNumber)
    {
        // Best-effort, per store. This runs during teardown — including after a FAILED or partial
        // startup, where an ancillary store's schema (e.g. wolverine_incoming_envelopes) may never
        // have been created and the release UPDATE throws (PostgreSQL 42P01, etc.). Releasing
        // ownership is itself optional: any envelopes left as owner_id = nodeNumber are reclaimed by
        // the durability agent's recovery polling on the next live node. So a single failing store
        // must not abort releasing the others, nor surface as an unhandled teardown error that masks
        // the real startup failure. See GH-3123.
        foreach (var store in _services.Enumerate().Select(x => x.Value))
        {
            try
            {
                await store.Admin.ReleaseAllOwnershipAsync(nodeNumber);
            }
            catch (Exception e)
            {
                _runtime.Logger.LogDebug(e,
                    "Error while releasing node ownership for message store {Store} during teardown. This is safe to ignore; ownership is reclaimed by recovery polling.",
                    store.Name);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        var stores = _services.Enumerate().Select(x => x.Value).ToArray();
        return stores.MaybeDisposeAllAsync();
    }

    public async Task ReplayDeadLettersAsync(Guid[] ids)
    {
        foreach (var database in await FindAllAsync())
        {
            await database.DeadLetters.ReplayAsync(new(ids), CancellationToken.None);
        }
    }

    public async Task ReplayDeadLettersAsync(string tenantId, Guid[] ids)
    {
        foreach (var tenantedMessageStore in _multiTenanted)
        {
            if (tenantedMessageStore.Source.Cardinality == DatabaseCardinality.DynamicMultiple)
            {
                await tenantedMessageStore.Source.RefreshAsync();
            }
            
            var tenanted = await tenantedMessageStore.Source.FindAsync(tenantId);
            if (tenanted != null)
            {
                await tenanted.DeadLetters.ReplayAsync(new(ids), CancellationToken.None);
            }
        }
    }

    public async Task DiscardDeadLettersAsync(Guid[] ids)
    {
        foreach (var database in await FindAllAsync())
        {
            await database.DeadLetters.DiscardAsync(new(ids), CancellationToken.None);
        }
    }

    public async Task DiscardDeadLettersAsync(string tenantId, Guid[] ids)
    {
        foreach (var tenantedMessageStore in _multiTenanted)
        {
            if (tenantedMessageStore.Source.Cardinality == DatabaseCardinality.DynamicMultiple)
            {
                await tenantedMessageStore.Source.RefreshAsync();
            }
            
            var tenanted = await tenantedMessageStore.Source.FindAsync(tenantId);
            if (tenanted != null)
            {
                await tenanted.DeadLetters.DiscardAsync(new(ids), CancellationToken.None);
            }
        }
    }

    private async Task<List<IMessageStore>> findStoresAsync(DeadLetterEnvelopeGetRequest request)
    {
        var list = new List<IMessageStore>();
        if (request.DatabaseUri != null)
        {
            var store = await FindDatabaseAsync(request.DatabaseUri);
            if (store != null)
            {
                list.Add(store);
            }
        }
        else if (request.TenantId != null)
        {
            foreach (var tenantedMessageStore in _multiTenanted)
            {
                var store = await tenantedMessageStore.Source.FindAsync(request.TenantId);
                if (store != null)
                {
                    list.Add(store);
                    continue;
                }
                
                if (tenantedMessageStore.Source.Cardinality == DatabaseCardinality.DynamicMultiple)
                {
                    await tenantedMessageStore.Source.RefreshAsync();
                }
            
                store = await tenantedMessageStore.Source.FindAsync(request.TenantId);
                if (store != null)
                {
                    list.Add(store);
                }
            }
        }
        else
        {
            list.AddRange(await FindAllAsync());
        }

        return list;
    }

    public async Task<IReadOnlyList<DeadLetterEnvelopeResults>> FetchDeadLetterEnvelopesAsync(
        DeadLetterEnvelopeGetRequest request, CancellationToken cancellation)
    {
        var query = new DeadLetterEnvelopeQuery
        {
            PageSize = (int)request.Limit,
            PageNumber = request.PageNumber,
            MessageType = request.MessageType,
            ExceptionType = request.ExceptionType,
            ExceptionMessage = request.ExceptionMessage,
            Replayable = request.Replayable,
            Range = new TimeRange(request.From, request.Until)
        };

        var stores = await findStoresAsync(request);
        var list = new List<DeadLetterEnvelopeResults>();
        foreach (var store in stores)
        {
            var result = await store.DeadLetters.QueryAsync(query, cancellation);
            result.DatabaseUri = store.Uri;
            foreach (var envelope in result.Envelopes)
            {
                envelope.TryReadData(_runtime);
            }
            
            list.Add(result);
        }

        return list;
    }


    public bool HasAncillaryStoreFor(Type applicationType)
    {
        return _ancillaryStores.Contains(applicationType);
    }

    /// <summary>
    /// Every marker type that identifies an ancillary store -- a Marten or Polecat store interface,
    /// or an EF Core DbContext enrolled with Enroll&lt;T&gt;(). Used to associate a handler with an
    /// ancillary store when it takes one of these as a dependency instead of naming it with an
    /// attribute. See GH-3870.
    /// </summary>
    internal IEnumerable<Type> AncillaryMarkerTypes()
    {
        return _ancillaryStores.Enumerate().Select(x => x.Key);
    }

    private ImHashMap<string, IMessageStore> _messageTypeToAncillaryStore = ImHashMap<string, IMessageStore>.Empty;

    /// <summary>
    /// Register a mapping from a message type name to an ancillary store.
    /// Used so that DurableReceiver can persist incoming envelopes in the
    /// correct ancillary store when the handler targets a different database.
    /// </summary>
    internal void MapMessageTypeToAncillaryStore(string messageTypeName, Type ancillaryMarkerType)
    {
        if (_ancillaryStores.TryFind(ancillaryMarkerType, out var store))
        {
            _messageTypeToAncillaryStore = _messageTypeToAncillaryStore.AddOrUpdate(messageTypeName, store);
        }
    }

    /// <summary>
    /// Try to find the ancillary store that should be used to persist an incoming
    /// envelope based on the handler's [MartenStore] attribute. Returns null if
    /// the message type's handler uses the main store.
    /// </summary>
    public IMessageStore? TryFindAncillaryStoreForMessageType(string? messageTypeName)
    {
        if (messageTypeName == null) return null;
        return _messageTypeToAncillaryStore.TryFind(messageTypeName, out var store) ? store : null;
    }

    private ImHashMap<string, IMessageStore> _endpointMessageTypeToAncillaryStore =
        ImHashMap<string, IMessageStore>.Empty;

    // Every (endpoint, message type) pair that a sticky handler chain has spoken for, including the
    // ones that target the MAIN store and therefore have no entry in the map above.
    private ImHashMap<string, bool> _endpointMessageTypeIsStickyRouted = ImHashMap<string, bool>.Empty;

    private static string endpointMessageTypeKey(Uri endpointUri, string messageTypeName)
    {
        return $"{endpointUri}|{messageTypeName}";
    }

    /// <summary>
    /// Register the store that owns a message type's inbox row <i>at one specific endpoint</i>. A message
    /// type handled by several sticky handlers has a different answer per endpoint, which the message
    /// type keyed map above cannot represent -- there the chains collide on one key and the last one
    /// registered wins for every endpoint. See GH-3886.
    /// </summary>
    /// <param name="ancillaryMarkerType">
    /// Null when the sticky chain targets the main store. The pair is still recorded so that
    /// <see cref="TryFindAncillaryStoreForMessageType(Uri?,string?)"/> answers "main" rather than falling
    /// through to a sibling endpoint's ancillary store.
    /// </param>
    internal void MapEndpointMessageTypeToAncillaryStore(Uri endpointUri, string messageTypeName,
        Type? ancillaryMarkerType)
    {
        var key = endpointMessageTypeKey(endpointUri, messageTypeName);

        // First chain registered wins, to agree with the handler that will actually run:
        // HandlerGraph.HandlerFor(messageType, endpoint) selects ByEndpoint.FirstOrDefault(), and this
        // loop walks the chains in that same order. Routing the envelope to a store chosen by a chain
        // that never executes is how GH-3870 left envelopes stranded in the wrong database.
        if (_endpointMessageTypeIsStickyRouted.Contains(key)) return;
        _endpointMessageTypeIsStickyRouted = _endpointMessageTypeIsStickyRouted.AddOrUpdate(key, true);

        if (ancillaryMarkerType != null && _ancillaryStores.TryFind(ancillaryMarkerType, out var store))
        {
            _endpointMessageTypeToAncillaryStore = _endpointMessageTypeToAncillaryStore.AddOrUpdate(key, store);
        }
    }

    /// <summary>
    /// Try to find the ancillary store that should persist an incoming envelope, preferring the answer
    /// registered for the endpoint the envelope actually arrived on. Falls back to the message type wide
    /// answer for any endpoint that has no sticky handler of its own. Returns null when the main store
    /// should be used.
    /// </summary>
    public IMessageStore? TryFindAncillaryStoreForMessageType(Uri? endpointUri, string? messageTypeName)
    {
        if (messageTypeName == null) return null;

        if (endpointUri != null)
        {
            var key = endpointMessageTypeKey(endpointUri, messageTypeName);

            if (_endpointMessageTypeToAncillaryStore.TryFind(key, out var byEndpoint))
            {
                return byEndpoint;
            }

            // A sticky handler here that targets the main store must not inherit a sibling endpoint's
            // ancillary store from the message type keyed fallback
            if (_endpointMessageTypeIsStickyRouted.Contains(key))
            {
                return null;
            }
        }

        return TryFindAncillaryStoreForMessageType(messageTypeName);
    }
}

public class InvalidWolverineStorageConfigurationException : Exception
{
    public InvalidWolverineStorageConfigurationException(string? message) : base(message)
    {
    }
}