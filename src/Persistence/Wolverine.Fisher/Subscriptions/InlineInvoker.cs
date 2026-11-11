using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Fisher;
using Wolverine.Persistence.EventSourcing.Subscriptions;
using Wolverine.Runtime;

namespace Wolverine.Fisher.Subscriptions;

/// <summary>
/// The Fisher subscription behind ProcessEventsWithWolverineHandlersInStrictOrder(). The behavior is the shared
/// <see cref="InlineEventInvoker"/> (GH-5143).
/// </summary>
internal class InlineInvoker : BatchSubscription
{
    private readonly InlineEventInvoker _invoker;

    public InlineInvoker(string subscriptionName, IWolverineRuntime runtime) : base(subscriptionName)
    {
        _invoker = new InlineEventInvoker(runtime);
    }

    public override Task ProcessEventsAsync(EventRange page, ISubscriptionController controller,
        IDocumentSession operations, IMessageBus bus,
        CancellationToken cancellationToken)
    {
        return _invoker.InvokeAsync(page, controller, bus, cancellationToken);
    }
}
