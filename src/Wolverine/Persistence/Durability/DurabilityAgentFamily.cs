using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.Persistence.Durability;

public class DurabilityAgentFamily : IAgentFamily
{
    private readonly Dictionary<Uri, IMessageStoreWithAgentSupport> _storeWithAgents = new();
    private readonly List<ITenantedMessageSource> _tenantedStores = new();

    public DurabilityAgentFamily(IWolverineRuntime runtime)
    {
        addStore(runtime.Storage);
        foreach (var ancillaryStore in runtime.AncillaryStores)
        {
            addStore(ancillaryStore);
        }
    }

    private void addStore(IMessageStore store)
    {
        if (store is IMessageStoreWithAgentSupport agentSupport)
        {
            _storeWithAgents.TryAdd(agentSupport.Uri, agentSupport);
        }
        else if (store is MultiTenantedMessageStore tenantedMessageStore)
        {
            if (tenantedMessageStore.Main is IMessageStoreWithAgentSupport master)
            {
                _storeWithAgents.TryAdd(master.Uri, master);
            }
            
            _tenantedStores.Add(tenantedMessageStore.Source);
        }
    }

    internal static async Task<IAgent> StartScheduledJobProcessing(IWolverineRuntime runtime)
    {
        var family = new DurabilityAgentFamily(runtime);
        
        // First, find all unique message stores
        var stores = await family.findUniqueMessageStores();
        var agents = stores.Select(x => x.StartScheduledJobs(runtime));
        return new CompositeAgent(new Uri("internal://scheduledjobs"), agents);
    }

    private async Task<IReadOnlyList<IMessageStore>> findUniqueMessageStores()
    {
        var stores = new Dictionary<Uri, IMessageStore>();
        foreach (var agentSupport in _storeWithAgents)
        {
            stores.TryAdd(agentSupport.Key, agentSupport.Value);
        }

        foreach (var tenantedStore in _tenantedStores)
        {
            if (!tenantedStore.AllActive().Any())
            {
                await tenantedStore.RefreshAsync();
            }

            foreach (var store in tenantedStore.AllActive())
            {
                stores.TryAdd(store.Uri, store);
            }
        }

        return stores.Values.ToList();
    }

    public string Scheme => PersistenceConstants.AgentScheme;

    public async ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
    {
        foreach (var tenantedStore in _tenantedStores)
        {
            await tenantedStore.RefreshAsync();
        }

        var fromTenantedSources = _tenantedStores.SelectMany(x =>
            x.AllActive().OfType<IMessageStoreWithAgentSupport>().Select(mas => mas.Uri));
        
        return _storeWithAgents.Select(x => x.Value.Uri)
            .Concat(fromTenantedSources)
            .Distinct().ToList();


    }

    public async ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
    {
        var store = await findStoreAsync(uri);
        if (store == null)
        {
            throw new InvalidAgentException($"Unknown durability agent '{uri}'");
        }

        return store.BuildAgent(wolverineRuntime);
    }

    private async ValueTask<IMessageStoreWithAgentSupport?> findStoreAsync(Uri uri)
    {
        if (_storeWithAgents.TryGetValue(uri, out var store))
        {
            return store;
        }
        
        // Do a first pass trying to find it w/o refreshing the multi-tenanted stores
        foreach (var tenantedStore in _tenantedStores)
        {
            store = tenantedStore.AllActive().OfType<IMessageStoreWithAgentSupport>()
                .FirstOrDefault(x => x.Uri == uri);

            if (store != null) return store;
        }
        
        // Do a 2nd pass, but this time force a refresh
        foreach (var tenantedStore in _tenantedStores)
        {
            await tenantedStore.RefreshAsync();
            store = tenantedStore.AllActive().OfType<IMessageStoreWithAgentSupport>()
                .FirstOrDefault(x => x.Uri == uri);

            if (store != null) return store;
        }

        return null;
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
}