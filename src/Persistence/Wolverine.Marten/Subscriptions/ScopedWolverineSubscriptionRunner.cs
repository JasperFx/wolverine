using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Daemon;
using Marten.Events.Daemon.Internals;
using Marten.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Persistence.EventSourcing.Subscriptions;
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
        // GH-5143: the same tenant as the singleton runner. This runner used to set none.
        var context = await SubscriptionMessageContext.StartAsync(_runtime,
            WolverineSubscriptionRunner.TenantFor(operations),
            c => new MartenEnvelopeTransaction((IDocumentSession)operations, c));

        // GH-5143: disposed by the callback, after the commit's flush. This was a `using`, so the scope
        // was gone before the flush ran.
        var scope = _services.CreateScope();
        try
        {
            var subscription = scope.ServiceProvider.GetRequiredService<T>();
            await subscription.ProcessEventsAsync(page, controller, operations, context, cancellationToken);
        }
        catch
        {
            // No commit will follow, so no callback will dispose it
            scope.Dispose();
            throw;
        }

        return new WolverineCallbackForCascadingMessages(context, scope);
    }
}