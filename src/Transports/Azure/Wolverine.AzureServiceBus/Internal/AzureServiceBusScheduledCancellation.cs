using Azure.Messaging.ServiceBus;
using Wolverine.Runtime;

namespace Wolverine.AzureServiceBus.Internal;

internal static class AzureServiceBusScheduledCancellation
{
    public static async ValueTask<ScheduledMessageCancellationStatus> CancelAsync(ServiceBusSender sender,
        long sequenceNumber, CancellationToken cancellationToken)
    {
        try
        {
            await sender.CancelScheduledMessageAsync(sequenceNumber, cancellationToken).ConfigureAwait(false);
            return ScheduledMessageCancellationStatus.CancellationAccepted;
        }
        catch (ServiceBusException e) when (e.Reason == ServiceBusFailureReason.MessageNotFound)
        {
            return ScheduledMessageCancellationStatus.NoLongerScheduled;
        }
    }
}
