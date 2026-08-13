using JasperFx.Core;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Fisher;
using Fisher.Subscriptions;
using Wolverine.Runtime;

namespace Wolverine.Fisher.Subscriptions;

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

    public override async Task<IDaemonChangeListener> ProcessEventsAsync(EventRange page, ISubscriptionController controller, IDocumentSession operations,
        CancellationToken cancellationToken)
    {
        var context = new MessageContext(_runtime);

        // Use the session's tenant id for multi-tenant support
        var tenantId = operations.TenantId;
        if (tenantId.IsNotEmpty() && tenantId != JasperFx.StorageConstants.DefaultTenantId)
        {
            context.TenantId = tenantId;
        }

        await context.EnlistInOutboxAsync(new FisherEnvelopeTransaction(operations, context));

        await _subscription.ProcessEventsAsync(page, controller, operations, context, cancellationToken);

        return new WolverineCallbackForCascadingMessages(context);
    }
}
