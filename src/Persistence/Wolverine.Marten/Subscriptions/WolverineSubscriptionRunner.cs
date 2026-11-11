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
using Wolverine.Persistence.EventSourcing.Subscriptions;
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
        var context = await SubscriptionMessageContext.StartAsync(_runtime, TenantFor(operations),
            c => new MartenEnvelopeTransaction((IDocumentSession)operations, c));

        await _subscription.ProcessEventsAsync(page, controller, operations, context, cancellationToken);

        return new WolverineCallbackForCascadingMessages(context);
    }

    /// <summary>
    ///     The tenant a page of a Marten subscription belongs to, shared by the singleton and scoped runners
    ///     (GH-5143: the scoped runner used to set none).
    /// </summary>
    internal static string? TenantFor(IDocumentOperations operations)
    {
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
        return operations.DocumentStore.Options.Tenancy.Cardinality != DatabaseCardinality.Single
            ? operations.Database.Identifier
            : null;
    }
}
