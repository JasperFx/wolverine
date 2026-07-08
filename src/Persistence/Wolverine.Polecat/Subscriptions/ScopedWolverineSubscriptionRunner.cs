using JasperFx.Core;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Polecat;
using Polecat.Services;
using Polecat.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;

namespace Wolverine.Polecat.Subscriptions;

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
        Version = subscription.Version;
        subscription.Filter(this);
        Options = subscription.Options;
    }

    public override async Task<global::Polecat.IChangeListener> ProcessEventsAsync(EventRange page, ISubscriptionController controller, IDocumentOperations operations,
        CancellationToken cancellationToken)
    {
        var context = new MessageContext(_runtime);
        await context.EnlistInOutboxAsync(new PolecatEnvelopeTransaction((IDocumentSession)operations, context));

        var scope = _services.CreateScope();
        var subscription = scope.ServiceProvider.GetRequiredService<T>();
        await subscription.ProcessEventsAsync(page, controller, operations, context, cancellationToken);

        return new ScopedWolverineCallbackForCascadingMessages(scope, context);
    }
}

internal class ScopedWolverineCallbackForCascadingMessages : global::Polecat.IChangeListener
{
    private readonly IServiceScope _scope;
    private readonly MessageContext _context;

    public ScopedWolverineCallbackForCascadingMessages(IServiceScope scope, MessageContext context)
    {
        _scope = scope;
        _context = context;
    }

    public async Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        try
        {
            await _context.FlushOutgoingMessagesAsync();
        }
        finally
        {
            _scope.Dispose();
        }
    }

    public Task BeforeCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        return Task.CompletedTask;
    }
}
