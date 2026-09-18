using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Daemon;
using Marten.Events.Daemon.Internals;
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
        Name = subscription.SubscriptionName;
        Version = subscription.SubscriptionVersion;
        subscription.Filter(this);
        Options = subscription.Options;
    }

    public override async Task<IChangeListener> ProcessEventsAsync(EventRange page, ISubscriptionController controller, IDocumentOperations operations,
        CancellationToken cancellationToken)
    {
        var context = new MessageContext(_runtime);

        // GH-4485. Only stamp the database identifier as the tenant when this store actually
        // spreads tenants across databases -- there the identifier IS the routing key that
        // MartenMessageDatabaseSource keys its per-tenant message databases by, so the outbox
        // needs it.
        //
        // ⚠️ Do NOT make this unconditional again (it was, from 6b54d7261 until GH-4485). For a
        // single-database store Database.Identifier is StoreOptions.StoreName -- which DEFAULTS to
        // "Main" (and defaulted to "Marten" before Marten 8.37.3/9.13.0), a value that is not a
        // tenant id at all. Every envelope published from a subscription inherited it
        // (MessageBus.PublishAsync does outbound.TenantId ??= TenantId), it propagated onto
        // cascading messages, and OutboxedSessionFactory then opened the downstream handler's
        // Marten session for tenant "Main". Marten's DefaultTenancy.GetTenant() accepts any tenant
        // id without complaint on a single-database store, so those handlers silently appended
        // events stamped tenant_id = 'Main' alongside data written as '*DEFAULT*' -- and the
        // moment such a store used EventAppendMode.Quick, mt_quick_append_events' tenant guard
        // failed EVERY append to a pre-existing stream with
        // "P0001: The tenantid does not match the existing stream".
        if (operations.DocumentStore.Options.Tenancy.Cardinality != DatabaseCardinality.Single)
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