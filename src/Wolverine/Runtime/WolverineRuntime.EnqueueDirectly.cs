using Microsoft.Extensions.Logging;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Runtime;

public sealed partial class WolverineRuntime
{
    public async ValueTask EnqueueDirectlyAsync(IReadOnlyList<Envelope> envelopes)
    {
        var groups = envelopes.GroupBy(x => x.Destination ?? TransportConstants.LocalUri).ToArray();
        foreach (var group in groups)
        {
            var listener = Endpoints.FindListenerCircuit(group.Key);
            if (listener != null)
            {
                await listener.EnqueueDirectlyAsync(group);
            }
            else
            {
                // For send-only endpoints (e.g. Azure Service Bus topics),
                // there is no listener circuit. Send through the sending agent instead.
                ISendingAgent sender;
                try
                {
                    sender = Endpoints.GetOrBuildSendingAgent(group.Key);
                }
                catch (UnknownTransportException e)
                {
                    // The envelopes here have already been read out of persistence and
                    // reassigned to this node, so throwing would both lose the rest of
                    // this batch and leave the offending rows stranded -- and the poller
                    // would rediscover them and throw again on every subsequent run. A
                    // destination whose transport this node cannot resolve is never going
                    // to become sendable here, so dead letter the envelopes instead.
                    // See https://github.com/JasperFx/wolverine/issues/3413.
                    await deadLetterUnknownDestinationAsync(group, e);
                    continue;
                }

                foreach (var envelope in group)
                {
                    await sender.EnqueueOutgoingAsync(envelope);
                }
            }
        }
    }

    private async Task deadLetterUnknownDestinationAsync(IEnumerable<Envelope> envelopes, UnknownTransportException exception)
    {
        foreach (var envelope in envelopes)
        {
            Logger.LogError(exception,
                "Moving envelope {Id} ({MessageType}) to dead letter storage because this node has no transport registered that can send to its destination {Destination}",
                envelope.Id, envelope.MessageType, envelope.Destination);

            try
            {
                var inbox = envelope.Store?.Inbox ?? Storage.Inbox;
                await inbox.MoveToDeadLetterStorageAsync(envelope, exception);
                MessageTracking.MovedToErrorQueue(envelope, exception);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Error trying to move envelope {Id} with the unknown destination {Destination} to dead letter storage",
                    envelope.Id, envelope.Destination);
            }
        }
    }
}
