using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace Wolverine.Transports.Sending;

/// <summary>
///     GH-5196. Under <see cref="TenantedIdBehavior.DeadLetterUnknownTenants" />, a message addressed to a tenant id
///     that no tenant registered with the transport matches is never sent: not to the default broker, not to another
///     tenant's. The sending agents call this before a message is stored or sent, and again from their send loop for
///     anything that reached it another way (a recovered outbox row, a scheduled message coming due)
/// </summary>
internal static class UnknownBrokerTenants
{
    /// <summary>
    ///     The exception to dead letter <paramref name="envelope" /> with, or null when <paramref name="sender" />
    ///     should send it
    /// </summary>
    public static UnknownBrokerTenantException? Check(ISender sender, Envelope envelope)
    {
        if (sender is TenantedSender tenanted && tenanted.IsUnknownTenantToDeadLetter(envelope))
        {
            return new UnknownBrokerTenantException(envelope.TenantId!, envelope.MessageType, tenanted.Destination);
        }

        return null;
    }

    /// <summary>
    ///     Write the dead letter for <paramref name="envelope" /> to the MAIN message store: there is no tenant
    ///     database for a tenant nobody registered. The envelope keeps its tenant id. Throws
    ///     <paramref name="exception" /> when the application has no message store, so the publish call fails
    ///     instead of the message quietly going nowhere
    /// </summary>
    public static async Task DeadLetterAsync(IWolverineRuntime? runtime, Envelope envelope,
        UnknownBrokerTenantException exception, ILogger logger)
    {
        if (runtime == null || runtime.Storage is NullMessageStore)
        {
            throw exception;
        }

        var store = runtime.Storage is MultiTenantedMessageStore multiTenanted
            ? multiTenanted.Main
            : runtime.Storage;

        await store.Inbox.MoveToDeadLetterStorageAsync(envelope, exception).ConfigureAwait(false);

        logger.LogError(exception,
            "Outgoing message {Name}#{Id} to {Destination} is addressed to unknown tenant '{TenantId}', so it was moved to dead letter storage instead of being sent",
            envelope.MessageType, envelope.Id, exception.Endpoint, exception.TenantId);

        runtime.MessageTracking.MovedToErrorQueue(envelope, exception);
    }
}
