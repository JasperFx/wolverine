using JasperFx.Core;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Events.Daemon;
using Marten.Events.Daemon.Internals;
using Marten.Internal.Sessions;
using Marten.Schema.Arguments;
using Marten.Services;
using Marten.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Runtime;

namespace Wolverine.Marten.Subscriptions;

internal class WolverineSubscriptionRunner : SubscriptionBase
{
    private readonly IWolverineSubscription _subscription;
    private readonly IWolverineRuntime _runtime;

    public WolverineSubscriptionRunner(IWolverineSubscription subscription, IWolverineRuntime runtime)
    {
        _subscription = subscription;
        _runtime = runtime;
        SubscriptionName = subscription.SubscriptionName;
        SubscriptionVersion = subscription.SubscriptionVersion;
        subscription.Filter(this);
        Options = subscription.Options;
    }

    public override async Task<IChangeListener> ProcessEventsAsync(EventRange page, ISubscriptionController controller, IDocumentOperations operations,
        CancellationToken cancellationToken)
    {
        var context = new MessageContext(_runtime);

        if (_runtime.Storage is MultiTenantedMessageDatabase)
        {
            context.TenantId = operations.Database.Identifier;
        }
        
        await context.EnlistInOutboxAsync(new MartenEnvelopeTransaction((IDocumentSession)operations, context));

        await _subscription.ProcessEventsAsync(page, controller, operations, context, cancellationToken);
        
        return new WolverineCallbackForCascadingMessages(context);
    }
}

internal class ScopedWolverineCallbackForCascadingMessages : IChangeListener
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
            _scope.SafeDispose();
        }
    }

    public Task BeforeCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        return Task.CompletedTask;
    }
}