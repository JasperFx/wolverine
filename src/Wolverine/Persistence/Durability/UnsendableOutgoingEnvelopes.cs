using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.Persistence.Durability;

/// <summary>
///     GH-5047. What the <see cref="DurabilityMode.ServerlessWithOutbox" /> inline send and durability pass do with an
///     outgoing envelope the transport reported as a serialization failure, such as an SQS message over the size limit
///     without fragmenting. Sending it again can never succeed, so it is moved to dead letter storage, where it can be
///     seen, rather than retried by every pass. GH-5055: a sending agent does the same in every other mode
///     (<see cref="Transports.Sending.SendingAgent.MarkSerializationFailureAsync" />)
/// </summary>
internal static class UnsendableOutgoingEnvelopes
{
    /// <summary>
    ///     Write the dead letter for <paramref name="envelope" />. Answers false, after logging, when that failed: the
    ///     caller then leaves the outbox row in place for a later pass, so nothing is lost
    /// </summary>
    public static async Task<bool> TryMoveToDeadLetterStorageAsync(IWolverineRuntime runtime, IMessageStore store,
        Envelope envelope, Exception exception, ILogger logger)
    {
        try
        {
            await store.Inbox.MoveToDeadLetterStorageAsync(envelope, exception).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.LogError(e,
                "Unable to move outgoing message {Name}#{Id} to {Destination}, which the transport can never send, to dead letter storage. It is left in the outbox",
                envelope.GetMessageTypeName(), envelope.Id, envelope.Destination);
            return false;
        }

        if (exception is UnknownBrokerTenantException unknownTenant)
        {
            logger.LogError(exception,
                "Outgoing message {Name}#{Id} to {Destination} is addressed to unknown tenant '{TenantId}', so it was moved to dead letter storage instead of being sent",
                envelope.GetMessageTypeName(), envelope.Id, envelope.Destination, unknownTenant.TenantId);
        }
        else
        {
            logger.LogError(exception,
                "Outgoing message {Name}#{Id} to {Destination} failed to serialize for its transport, so it was moved to dead letter storage",
                envelope.GetMessageTypeName(), envelope.Id, envelope.Destination);
        }
        runtime.MessageTracking.MovedToErrorQueue(envelope, exception);

        return true;
    }
}

/// <summary>
///     GH-5055. Recorded with the dead letter of an outgoing envelope its transport reported as one it can never send,
///     like an envelope the transport's mapper threw on. The transport logged the original error when it happened
/// </summary>
internal class UnsendableEnvelopeException : Exception
{
    public UnsendableEnvelopeException(Envelope envelope, Uri destination) : base(
        $"The transport for {destination} can never send envelope {envelope}, because it could not be mapped or serialized to a broker message. The transport's error log has the original exception")
    {
    }
}
