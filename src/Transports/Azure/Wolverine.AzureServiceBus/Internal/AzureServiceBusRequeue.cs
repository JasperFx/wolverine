using Azure.Messaging.ServiceBus;
using Wolverine.Transports.Sending;

namespace Wolverine.AzureServiceBus.Internal;

/// <summary>
/// GH-5767. Azure Service Bus has no native "put it back at the end", so every listener requeues (and reschedules)
/// by sending a COPY to the entity and completing the original. This is the one place that does it, for all four
/// listeners.
/// </summary>
internal static class AzureServiceBusRequeue
{
    /// <summary>
    /// The Wolverine envelope id of a requeue copy. The copy gets a <c>MessageId</c> of its own, so the receiving
    /// side reads the envelope id from here instead.
    /// </summary>
    internal const string EnvelopeIdKey = "wolverine-envelope-id";

    /// <summary>
    /// Send the copy FIRST and complete the original only once Azure Service Bus has accepted it. Completing first
    /// lost the message whenever the send failed or the process exited in between, and a NativeAck endpoint
    /// requeues every envelope still in its lanes on a graceful shutdown. If the send fails the original stays
    /// locked, the lock lapses and the broker redelivers it, so the worst case is a duplicate rather than a loss.
    /// </summary>
    internal static async Task RequeueAsync(Envelope envelope, ISender requeue, CancellationToken token)
    {
        if (envelope is not AzureServiceBusEnvelope e)
        {
            await requeue.SendAsync(envelope);
            return;
        }

        if (!e.RequeueCopySent)
        {
            // The same id on every retry of this requeue, so an ambiguous send (a timeout after the broker took
            // it) is deduplicated against its retry instead of making two copies
            e.RequeueMessageId ??= Guid.NewGuid().ToString();

            await requeue.SendAsync(e);

            // Before the complete: a failed complete retries this, which must not send a second copy
            e.RequeueCopySent = true;
        }

        if (!e.IsCompleted)
        {
            await e.CompleteAsync(token);
        }
    }

    /// <summary>
    /// Applied by the sender after the envelope mapper ran, so it holds for interop mappers too.
    /// </summary>
    internal static void StampCopy(Envelope envelope, ServiceBusMessage message)
    {
        if (envelope is not AzureServiceBusEnvelope { RequeueMessageId: { } messageId } e) return;

        // Reusing the original's MessageId made an entity with duplicate detection accept the send and then
        // silently drop it inside the detection window
        message.MessageId = messageId;
        message.ApplicationProperties[EnvelopeIdKey] = e.Id.ToString();

        // A session-enabled entity refuses a message without a SessionId. Only the default mapper carries the
        // received SessionId through Envelope.GroupId.
        if (string.IsNullOrEmpty(message.SessionId) && !string.IsNullOrEmpty(e.AzureMessage.SessionId))
        {
            message.SessionId = e.AzureMessage.SessionId;
        }
    }
}
