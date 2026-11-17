using JasperFx.Core.Reflection;
using JasperFx.Events;
using Polecat;
using Wolverine.Runtime.Agents;

namespace Wolverine.Polecat.Distribution;

/// <summary>
///     The projection coordinator of a Polecat store under
///     <see cref="PolecatIntegration.UseWolverineManagedEventSubscriptionDistribution" />. Wolverine's
///     <see cref="EventSubscriptionAgentFamily" /> runs each projection and subscription as an agent, so
///     operator tooling pauses, restarts and rewinds them the same way on Marten, Polecat and Fisher.
/// </summary>
/// <remarks>
///     GH-5141: the coordinator itself is Wolverine core's, written once against JasperFx's
///     <see cref="JasperFx.Events.Daemon.IProjectionCoordinator" />. This type only adds Polecat's own
///     <see cref="IProjectionCoordinator" />, whose members the core coordinator already has.
/// </remarks>
internal class WolverineProjectionCoordinator<T> : WolverineProjectionCoordinator, IProjectionCoordinator<T>
    where T : IDocumentStore
{
    public WolverineProjectionCoordinator(EventSubscriptionAgentFamily agents, T store) : base(agents, store)
    {
    }
}

internal class WolverineProjectionCoordinator :
    Wolverine.Persistence.EventSourcing.Distribution.WolverineProjectionCoordinator, IProjectionCoordinator
{
    public WolverineProjectionCoordinator(EventSubscriptionAgentFamily agents, IDocumentStore store)
        : base(agents, store.As<IEventStore>())
    {
    }
}
