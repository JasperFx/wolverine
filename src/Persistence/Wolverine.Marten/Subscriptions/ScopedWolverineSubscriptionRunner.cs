using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Daemon;
using Marten.Events.Daemon.Internals;
using Marten.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;

namespace Wolverine.Marten.Subscriptions;

internal class ScopedWolverineSubscriptionRunner<T> : SubscriptionBase where T : IWolverineSubscription
{
    private readonly IServiceProvider _services;
    private readonly IWolverineRuntime _runtime;

    public ScopedWolverineSubscriptionRunner(IServiceProvider services, IWolverineRuntime runtime)
    {
        _services = services;
        _runtime = runtime;

        using var scope = services.CreateScope();
        var subscription = scope.ServiceProvider.GetRequiredService<T>();
        Name = subscription.SubscriptionName;
        Version = subscription.SubscriptionVersion;
        subscription.Filter(this);
        Options = subscription.Options;
    }

    public override async Task<IChangeListener> ProcessEventsAsync(EventRange page, ISubscriptionController controller, IDocumentOperations operations,
        CancellationToken cancellationToken)
    {
        var context = new MessageContext(_runtime);
        await context.EnlistInOutboxAsync(new MartenEnvelopeTransaction((IDocumentSession)operations, context));

        using var scope = _services.CreateScope();
        var subscription = scope.ServiceProvider.GetRequiredService<T>();
        await subscription.ProcessEventsAsync(page, controller, operations, context, cancellationToken);

        return new ScopedWolverineCallbackForCascadingMessages(scope, context);
    }
}