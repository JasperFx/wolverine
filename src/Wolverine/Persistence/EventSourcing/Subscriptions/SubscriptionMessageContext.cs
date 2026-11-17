using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace Wolverine.Persistence.EventSourcing.Subscriptions;

/// <summary>
///     The store agnostic half of a Wolverine event subscription runner on Marten, Polecat or Fisher:
///     builds the page's <see cref="MessageContext" />, stamps its tenant and enlists it in the daemon's
///     session, then flushes it once the daemon commits. GH-5143.
/// </summary>
/// <remarks>
///     Each store still has its own runner class, because each derives from that store's own
///     <c>SubscriptionBase</c> and returns that store's own change listener. Only the behavior moved here,
///     which is where the three had drifted apart: the scoped runners set no tenant at all, and Marten's
///     scoped runner disposed its service scope before the after-commit flush.
/// </remarks>
internal static class SubscriptionMessageContext
{
    /// <summary>
    ///     Start the message context for one page of events.
    /// </summary>
    /// <param name="runtime">The Wolverine runtime.</param>
    /// <param name="tenantId">
    ///     The tenant the store says this page belongs to, or null. An empty value or the default tenant id
    ///     leaves the context untenanted, so messages published from the page do not inherit a tenant id
    ///     that is not one.
    /// </param>
    /// <param name="transactionSource">Builds the store specific envelope transaction over the daemon's session.</param>
    public static async ValueTask<MessageContext> StartAsync(IWolverineRuntime runtime, string? tenantId,
        Func<MessageContext, IEnvelopeTransaction> transactionSource)
    {
        var context = new MessageContext(runtime);

        if (tenantId.IsNotEmpty() && tenantId != StorageConstants.DefaultTenantId)
        {
            context.TenantId = tenantId;
        }

        await context.EnlistInOutboxAsync(transactionSource(context)).ConfigureAwait(false);

        return context;
    }

    /// <summary>
    ///     Flush a page's outgoing messages after the daemon commits, then dispose the page's service scope,
    ///     if it has one. The scope has to outlive the flush: the subscription it resolved may own services
    ///     the outgoing messages still need.
    /// </summary>
    public static async Task AfterCommitAsync(MessageContext context, IServiceScope? scope)
    {
        try
        {
            await context.FlushOutgoingMessagesAsync().ConfigureAwait(false);
        }
        finally
        {
            scope?.SafeDispose();
        }
    }
}
