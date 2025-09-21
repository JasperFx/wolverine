using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.Persistence;

public class MessageStoreCollection : IAgentFamily, IAsyncDisposable
{
    private readonly IWolverineRuntime _runtime;
    private readonly List<MultiTenantedMessageStore> _multiTenanted = new();
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

        if (!_runtime.Options.Durability.DurabilityAgentEnabled)
        {
            Main = new NullMessageStore();
        }
        else if (_services.Count() == 1)
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

        if (!_runtime.Options.Durability.DurabilityAgentEnabled)
        {
            Main = new NullMessageStore();
            return;
        }
        
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
        await tenantedMessageStore.Source.RefreshAsync();

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
        assignments.DistributeEvenly(Scheme);
        return ValueTask.CompletedTask;
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
        multiTenanted = _multiTenanted.FirstOrDefault(x => x.Main.Uri == store.Uri);
        return multiTenanted != null;
    }

    public async Task ReleaseAllOwnershipAsync(int nodeNumber)
    {
        foreach (var store in  _services.Enumerate().Select(x => x.Value))
        {
            await store.Admin.ReleaseAllOwnershipAsync(nodeNumber);
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
}

public class InvalidWolverineStorageConfigurationException : Exception
{
    public InvalidWolverineStorageConfigurationException(string? message) : base(message)
    {
    }
}