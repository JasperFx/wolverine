using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;

namespace Wolverine.AzureServiceBus.Internal;

/// <summary>
/// GH-4012 item 3. The settle path shared by both Azure Service Bus listeners, carrying the two
/// protections RabbitMQ already had and Azure Service Bus did not: the envelope's ack budget, and
/// terminal-failure classification.
/// </summary>
/// <remarks>
/// <para>Without classification a permanent failure burns the whole retry budget before being
/// dropped. Azure Service Bus makes the distinction first class -- <see cref="ServiceBusException.IsTransient"/>
/// -- so unlike RabbitMQ there is no broker text to string-match. That matters: the Rabbit needle was
/// wrong for a long time precisely because it was a string, and nothing failed loudly when it stopped
/// matching.</para>
///
/// <para>Both give-up paths deliberately swallow rather than rethrow. Swallowing is what stops the
/// <c>RetryBlock</c>; the delivery is simply left unsettled, its lock lapses, and the broker
/// redelivers -- the same recovery the RabbitMQ unknown-delivery-tag branch relies on.</para>
/// </remarks>
internal static class AzureServiceBusSettlement
{
    internal static async Task CompleteAsync(AzureServiceBusEnvelope envelope, int maximumAckAttempts,
        ILogger logger, CancellationToken token)
    {
        if (!envelope.TryRecordAckAttempt(maximumAckAttempts))
        {
            logger.LogWarning(
                "Giving up on completing Azure Service Bus message for envelope {EnvelopeId} after {AckAttempts} attempts; leaving it for broker redelivery",
                envelope.Id, envelope.AckAttempts);
            return;
        }

        try
        {
            await envelope.CompleteAsync(token);
        }
        catch (ServiceBusException e) when (!e.IsTransient)
        {
            // Terminal. MessageLockLost is the common one: the lock is gone, so no later attempt on
            // any receiver can settle this delivery. Retrying only delays the redelivery that is
            // already coming.
            logger.LogInformation(
                "Discarding a terminal Azure Service Bus settle failure ({Reason}) for envelope {EnvelopeId}; the broker will redeliver it",
                e.Reason, envelope.Id);
        }
    }
}
