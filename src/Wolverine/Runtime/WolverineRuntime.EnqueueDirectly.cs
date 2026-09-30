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
            // GH-4700. Has to come before FindListenerCircuit, which answers yes for ANY local:// address.
            // A global partition's companion local queue exists on every node by design (GH-3856), so
            // "I can build a circuit here" is not the same question as "this slot is mine to run". The
            // scheduled poller is deliberately unfiltered -- it takes every due row behind one per-database
            // advisory lock, and GH-4645 depends on it promoting rows for destinations it does not serve --
            // so this is where ownership has to be settled.
            var slotUri = Endpoints.GlobalPartitionSlotFor(group.Key);
            if (slotUri != null && !thisNodeOwnsPartitionSlot(slotUri))
            {
                await forwardToPartitionSlotAsync(group, slotUri);
                continue;
            }

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

    /// <summary>
    /// GH-4700. The same question GlobalPartitionedRoute asks at send time: is this slot's exclusive
    /// listener accepting HERE. Asked of the external endpoint, because the companion local queue cannot
    /// answer it.
    /// </summary>
    private bool thisNodeOwnsPartitionSlot(Uri slotUri)
    {
        return Endpoints.FindListeningAgent(slotUri) is { Status: ListeningStatus.Accepting };
    }

    /// <summary>
    /// GH-4700. A scheduled retry parks in the inbox at the address it was received on, and for a message
    /// that took GlobalPartitionedRoute's local shortcut that address is the companion local queue. Slot
    /// ownership at reschedule time says nothing about who owns it when the retry comes due -- the same
    /// reasoning as GH-4673, reached through the retry path instead of the send path -- so a non-owner
    /// hands it to the slot rather than running it. The owning node's listener picks it up through the
    /// existing bridge.
    ///
    /// The ordering is the whole trick, and both halves are load-bearing:
    ///
    /// The destination is rewritten BEFORE the send because DurableReceiver stamps the listener's address
    /// only when the envelope has none (<c>Destination ??= Uri</c>). Forwarding it untouched would park the
    /// row at the companion queue address on the OWNING node too, which moves the bug rather than fixing it.
    ///
    /// The parked address is captured BEFORE the rewrite because the inbox row has to be retired at the
    /// address it was actually written under -- <c>received_at</c>, which is part of the identity under
    /// MessageIdentity.IdAndDestination. Deleting by the live destination after the send would miss it and
    /// strand the row, which is exactly the GH-4645 data loss. OutgoingMessageBatch also assigns
    /// <c>Destination</c> itself, so the live value cannot be trusted once the envelope is handed over.
    /// </summary>
    private async Task forwardToPartitionSlotAsync(IEnumerable<Envelope> group, Uri slotUri)
    {
        ISendingAgent sender;
        try
        {
            sender = Endpoints.GetOrBuildSendingAgent(slotUri);
        }
        catch (UnknownTransportException e)
        {
            await deadLetterUnknownDestinationAsync(group, e);
            return;
        }

        foreach (var envelope in group)
        {
            var parkedAt = envelope.Destination;

            Logger.LogDebug(
                "Forwarding envelope {Id} ({MessageType}) from the global partition companion queue {Parked} to slot {Slot}, which this node does not own",
                envelope.Id, envelope.MessageType, parkedAt, slotUri);

            envelope.Destination = slotUri;

            await sender.EnqueueOutgoingAsync(envelope);
            await retireForwardedInboxRowAsync(envelope, parkedAt);
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
    /// <param name="parkedAt">
    /// GH-4700. The address the inbox row was written under, when that is not the envelope's current
    /// destination -- the partition-slot forward above rewrites the destination before sending. The delete
    /// matches on id AND received_at, so it has to be given the original address; a stand-in envelope
    /// carries it rather than mutating the live one back, which would race the outgoing send.
    /// </param>
    private async Task retireForwardedInboxRowAsync(Envelope envelope, Uri? parkedAt = null)
    {
        try
        {
            var inbox = envelope.Store?.Inbox ?? Storage.Inbox;

            var target = parkedAt == null || parkedAt == envelope.Destination
                ? envelope
                : new Envelope { Id = envelope.Id, Destination = parkedAt };

            await inbox.DeleteIncomingEnvelopeAsync(target);
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
