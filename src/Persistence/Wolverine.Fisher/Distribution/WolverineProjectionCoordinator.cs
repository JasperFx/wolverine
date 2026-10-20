using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using Fisher;
using Wolverine.Runtime.Agents;

namespace Wolverine.Fisher.Distribution;

/// <summary>
///     GH-5094: the projection coordinator of a Fisher store under
///     <see cref="FisherIntegration.UseWolverineManagedEventSubscriptionDistribution" />. Wolverine's
///     <see cref="EventSubscriptionAgentFamily" /> runs each projection and subscription as an agent, so
///     operator tooling pauses, restarts and rewinds them the same way on Fisher as on Marten and Polecat.
/// </summary>
internal class WolverineProjectionCoordinator<T> : WolverineProjectionCoordinator, IProjectionCoordinator<T>
    where T : IDocumentStore
{
    public WolverineProjectionCoordinator(EventSubscriptionAgentFamily agents, T store) : base(agents, store)
    {
    }
}

internal class WolverineProjectionCoordinator : IProjectionCoordinator
{
    private readonly EventStoreAgents _storeAgents;

    public WolverineProjectionCoordinator(EventSubscriptionAgentFamily agents, IDocumentStore store)
    {
        _storeAgents = agents.FindStore(store.As<IEventStore>().Identity);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return _storeAgents.StartAllAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return _storeAgents.StopAllAsync(cancellationToken);
    }

    public ValueTask<IProjectionDaemon> DaemonForMainDatabaseAsync()
    {
        return _storeAgents.DaemonForMainDatabaseAsync();
    }

    // GH-5092: the JasperFx IProjectionCoordinator member, now that this interface extends it
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
