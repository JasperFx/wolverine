using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using Marten;
using Marten.Events.Daemon.Coordination;

namespace Wolverine.Marten.Distribution;

// IProjectionCoordinator<T> / IProjectionCoordinator now exist in both
// Marten.Events.Daemon.Coordination and JasperFx.Events.Daemon (lifted by
// the dedupe pillar in JasperFx.Events 2.0.0-alpha.8+). Wolverine.Marten
// implements the Marten-side contract; qualify with `global::Marten...` to
// break the namespace-vs-namespace ambiguity inside Wolverine.Marten.*.
internal class WolverineProjectionCoordinator<T> : WolverineProjectionCoordinator,
    global::Marten.Events.Daemon.Coordination.IProjectionCoordinator<T> where T : class, IDocumentStore
{
    public WolverineProjectionCoordinator(EventSubscriptionAgentFamily agents, T store) : base(agents, store)
    {
    }
}

internal class WolverineProjectionCoordinator : global::Marten.Events.Daemon.Coordination.IProjectionCoordinator
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