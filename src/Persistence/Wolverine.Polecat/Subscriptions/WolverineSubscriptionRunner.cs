using JasperFx.Core;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Polecat;
using Polecat.Subscriptions;
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

    public override async Task<global::Polecat.Subscriptions.IChangeListener> ProcessEventsAsync(EventRange page, ISubscriptionController controller, IDocumentOperations operations,
        CancellationToken cancellationToken)
    {
        var context = new MessageContext(_runtime);

        // Use the session's tenant id for multi-tenant support
        var tenantId = operations.TenantId;
        if (tenantId.IsNotEmpty() && tenantId != global::Polecat.Tenancy.DefaultTenantId)
        {
            context.TenantId = tenantId;
        }

        await context.EnlistInOutboxAsync(new PolecatEnvelopeTransaction((IDocumentSession)operations, context));

        await _subscription.ProcessEventsAsync(page, controller, operations, context, cancellationToken);

        return new WolverineCallbackForCascadingMessages(context);
    }
}
