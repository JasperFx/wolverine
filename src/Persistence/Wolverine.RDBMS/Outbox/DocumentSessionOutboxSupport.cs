using JasperFx.Core;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace Wolverine.RDBMS.Outbox;

/// <summary>
/// GH-5144. The parts of opening an outboxed document session that Marten's, Polecat's and Fisher's
/// <c>OutboxedSessionFactory</c> and envelope transactions each wrote for themselves.
/// </summary>
internal static class DocumentSessionOutboxSupport
{
    /// <summary>
    /// The relational message store a document store's outbox writes envelope rows into. A multi-tenanted runtime
    /// wraps it in a <see cref="MultiTenantedMessageStore" />, whose <c>Main</c> is the store wanted, so a direct
    /// cast fails in that mode (GH-2668).
    /// </summary>
    public static T? TryResolveMessageStore<T>(IMessageStore? store) where T : class, IMessageDatabase
    {
        return store switch
        {
            T direct => direct,
            MultiTenantedMessageStore { Main: T main } => main,
            _ => null
        };
    }

    /// <summary>
    /// The causation id a session opened for this message stamps on what it writes. A per-message override supplied
    /// through <c>DeliveryOptions.CausationId</c> (envelope header "causation-id") wins over the default
    /// ConversationId-based chain, which is how a projection that publishes with its own causation gets it onto the
    /// events the command's handler writes (GH-4931: the same causation on every store).
    /// </summary>
    public static string? CausationIdFor(MessageContext context)
    {
        if (context.Envelope is { } env
            && env.Headers.TryGetValue(EnvelopeConstants.CausationIdKey, out var headerCausationId)
            && headerCausationId.IsNotEmpty())
        {
            return headerCausationId;
        }

        return context.ConversationId != Guid.Empty ? context.ConversationId.ToString() : null;
    }

    /// <summary>
    /// The user name a session opened for this message records as the last modifier, or null to leave the
    /// session's own.
    /// </summary>
    public static string? UserNameFor(MessageContext context)
    {
        return context.Envelope?.UserName ?? context.UserName;
    }
}
