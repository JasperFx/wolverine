using JasperFx.Core.Reflection;
using JasperFx.Events;
using Marten;
using Wolverine.Runtime.Agents;

namespace Wolverine.Marten.Distribution;

// GH-5141: the coordinator itself is Wolverine core's, written once against JasperFx's IProjectionCoordinator
// for Marten, Polecat and Fisher. These types only add Marten's own spelling of the interface, which carries
// no members of its own, so that code resolving global::Marten...IProjectionCoordinator keeps finding it.
internal class WolverineProjectionCoordinator<T> : WolverineProjectionCoordinator,
    global::Marten.Events.Daemon.Coordination.IProjectionCoordinator<T> where T : class, IDocumentStore
{
    public WolverineProjectionCoordinator(EventSubscriptionAgentFamily agents, T store) : base(agents, store)
    {
    }
}

internal class WolverineProjectionCoordinator :
    Wolverine.Persistence.EventSourcing.Distribution.WolverineProjectionCoordinator,
    global::Marten.Events.Daemon.Coordination.IProjectionCoordinator
{
    public WolverineProjectionCoordinator(EventSubscriptionAgentFamily agents, IDocumentStore store)
        : base(agents, store.As<IEventStore>())
    {
    }
}
