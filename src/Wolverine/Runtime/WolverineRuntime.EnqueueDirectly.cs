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
                    await retireForwardedInboxRowAsync(envelope);
                }
            }
        }
    }

    // GH-4645. Everything reaching this method was read out of the inbox -- the scheduled poll promotes a
    // row to Incoming, takes ownership of it, and hands it here. The listener branch above settles that row
    // as part of handling the message (DurableReceiver.CompleteAsync -> MarkIncomingEnvelopeAsHandledAsync),
    // but the sender branch never did: this node forwards the envelope to a transport it does not listen to
    // and walks away, leaving an Incoming row owned by a live node that nothing will ever retire.
    //
    // On the database-backed queue transports that is not merely untidy, it destroys the message. The
    // anti-duplicate probe those listeners run before every pop (GH-4316) deletes any queue row whose id is
    // already in the inbox at that queue's address, with no status filter -- and the row we just forwarded
    // *is* at that address, because a scheduled envelope parks in the inbox under its eventual destination
    // (IEnvelopeTransaction.PersistAsync). So the owning node deletes the queue row on its very next poll and
    // the message is never handled, with nothing logged and nothing dead lettered. That is the reported
    // symptom: a delayed cascading message to a partitioned PostgreSQL queue owned by another node simply
    // vanishing. Marking the row Handled instead of deleting it does not help -- a retained Handled row
    // inside KeepAfterMessageHandling matches the same probe.
    //
    // On every other transport the same orphan row is a quieter bug in its own right: once this node dies
    // and its rows are released back to AnyNode, recovery re-enqueues a message that was already delivered.
    private async Task retireForwardedInboxRowAsync(Envelope envelope)
    {
        try
        {
            var inbox = envelope.Store?.Inbox ?? Storage.Inbox;
            await inbox.DeleteIncomingEnvelopeAsync(envelope);
        }
        catch (Exception e)
        {
            // Deliberately not rethrowing. The envelope is already on its way to the destination, so failing
            // here would strand the rest of the batch and the poller would forward this one again on its
            // next pass. A stranded row is recoverable by hand; a send loop is not.
            Logger.LogError(e,
                "Error trying to delete the inbox row for envelope {Id} ({MessageType}) after forwarding it to {Destination}",
                envelope.Id, envelope.MessageType, envelope.Destination);
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
