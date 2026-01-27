using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using Marten;
using Marten.Events.Daemon.Coordination;

namespace Wolverine.Marten.Distribution;

internal class WolverineProjectionCoordinator<T> : WolverineProjectionCoordinator, IProjectionCoordinator<T> where T : IDocumentStore
{
    public WolverineProjectionCoordinator(EventSubscriptionAgentFamily agents, T store) : base(agents, store)
    {
    }
}

internal class WolverineProjectionCoordinator : IProjectionCoordinator
{
    private readonly EventSubscriptionAgentFamily _agents;
    private readonly EventStoreIdentity _identity;
    private readonly EventStoreAgents _storeAgents;

    public WolverineProjectionCoordinator(EventSubscriptionAgentFamily agents, IDocumentStore store)
    {
        _agents = agents;
        _identity = store.As<IEventStore>().Identity;
        _storeAgents = _agents.FindStore(_identity);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return _storeAgents.StartAllAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    { 
        return _storeAgents.StopAllAsync(cancellationToken);
    }

    public IProjectionDaemon DaemonForMainDatabase()
    {
        return _storeAgents.DaemonForMainDatabase();
    }

    public ValueTask<IProjectionDaemon> DaemonForDatabase(string databaseIdentifier)
    {
        return _storeAgents.DaemonForDatabase(databaseIdentifier);
    }

    public ValueTask<IReadOnlyList<IProjectionDaemon>> AllDaemonsAsync()
    {
        return _storeAgents.AllDaemonsAsync();
    }

    public Task PauseAsync()
    {
        return StopAsync(CancellationToken.None);
    }

    public Task ResumeAsync()
    {
        return StartAsync(CancellationToken.None);
    }
}