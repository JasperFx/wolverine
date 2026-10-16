using Microsoft.Extensions.Logging;
using Wolverine.Runtime;

namespace Wolverine.Persistence.Durability;

/// <summary>
///     GH-5047. What the <see cref="DurabilityMode.ServerlessWithOutbox" /> inline send and durability pass do with an
///     outgoing envelope the transport reported as a serialization failure, such as an SQS message over the size limit
///     without fragmenting. Sending it again can never succeed, so it is moved to dead letter storage, where it can be
///     seen, rather than retried by every pass. In every other mode a durable sending agent deletes such a row
///     (<see cref="DurableSendingAgent.MarkSerializationFailureAsync" />)
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
                "Unable to move outgoing message {Envelope} to {Destination}, which the transport can never send, to dead letter storage. It is left in the outbox",
                envelope, envelope.Destination);
            return false;
        }

        logger.LogError(exception,
            "Outgoing message {Envelope} to {Destination} failed to serialize for its transport, so it was moved to dead letter storage",
            envelope, envelope.Destination);
        runtime.MessageTracking.MovedToErrorQueue(envelope, exception);

        return true;
    }
}
