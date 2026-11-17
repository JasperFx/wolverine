using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Polecat;
using Polecat.Subscriptions;
using Wolverine.Persistence.EventSourcing.Subscriptions;
using Wolverine.Runtime;

namespace Wolverine.Polecat.Subscriptions;

internal class WolverineSubscriptionRunner : SubscriptionBase
{
    private readonly IWolverineSubscription _subscription;
    private readonly IWolverineRuntime _runtime;

    public WolverineSubscriptionRunner(IWolverineSubscription subscription, IWolverineRuntime runtime)
    {
        _subscription = subscription;
        _runtime = runtime;
        Name = subscription.SubscriptionName;
        Version = subscription.Version;
        subscription.Filter(this);
        Options = subscription.Options;
    }

    public override async Task<global::Polecat.IChangeListener> ProcessEventsAsync(EventRange page, ISubscriptionController controller, IDocumentOperations operations,
        CancellationToken cancellationToken)
    {
        // The session's tenant id, for multi-tenant support
        var context = await SubscriptionMessageContext.StartAsync(_runtime, operations.TenantId,
            c => new PolecatEnvelopeTransaction((IDocumentSession)operations, c));

        await _subscription.ProcessEventsAsync(page, controller, operations, context, cancellationToken);

        return new WolverineCallbackForCascadingMessages(context);
    }
}
