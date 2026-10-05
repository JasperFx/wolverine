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
            // GH-4822. Every envelope here was promoted by a scheduled poller that has already committed its
            // rows as Incoming and owned by this node, and inbox recovery only ever releases rows owned by a
            // node it has proven dead. Letting one destination's failure escape would leave its rows -- and
            // those of every destination after it in the batch -- owned by a live node forever, with nothing
            // logged against them and nothing dead lettered. Same remedy as GH-3680 on the recovery side:
            // hand them back to any node so recovery tries again.
            //
            // Only what was not handed over yet goes back: an envelope already forwarded has had its row retired,
            // and releasing it too would let recovery run it a second time. The listener branch hands a group
            // over as a whole and cannot say how far it got, so a failure there releases all of it -- the same
            // trade GH-3680 makes, and the realistic failure (a listener with no receiver) throws before the
            // first envelope.
            var handedOver = new HashSet<Envelope>();
            try
            {
                await enqueueDirectlyAsync(group, handedOver);
            }
            catch (Exception e)
            {
                var stranded = group.Where(x => !handedOver.Contains(x)).ToArray();

                Logger.LogError(e,
                    "Error trying to enqueue {Count} promoted scheduled envelopes for {Destination}. Releasing them back to any node so that they are recovered later",
                    stranded.Length, group.Key);

                await releaseToAnyNodeAsync(stranded, group.Key);
            }
        }
    }

    private async Task enqueueDirectlyAsync(IGrouping<Uri, Envelope> group, ISet<Envelope> handedOver)
    {
        // GH-4700. Has to come before FindListenerCircuit, which answers yes for ANY local:// address.
        // A global partition's companion local queue exists on every node by design (GH-3856), so
        // "I can build a circuit here" is not the same question as "this slot is mine to run". The
        // scheduled poller is deliberately unfiltered -- it takes every due row behind one per-database
        // advisory lock, and GH-4645 depends on it promoting rows for destinations it does not serve --
        // so this is where ownership has to be settled.
        //
        // GH-4822. A scheduled message to a global partition parks at the external slot's own address
        // (GH-4673), which is not a companion queue, so it needs the same question asked of the slot
        // itself. A node that never owned the slot would fall through to the sender branch below and
        // forward correctly anyway, but one that USED to own it still has the stopped listening agent
        // registered, and FindListenerCircuit hands the envelopes to a listener with no receiver.
        var slotUri = Endpoints.GlobalPartitionSlotFor(group.Key)
                      ?? (Endpoints.IsGlobalPartitionSlot(group.Key) ? group.Key : null);
        if (slotUri != null && !thisNodeOwnsPartitionSlot(slotUri))
        {
            await forwardToPartitionSlotAsync(group, slotUri, handedOver);
            return;
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
                return;
            }

            foreach (var envelope in group)
            {
                await HandOverAsync(sender, envelope, handedOver);
            }
        }
    }

    /// <summary>
    /// GH-4824. Hand one promoted envelope to a sending agent and retire its inbox row, in the only order
    /// that can neither lose the message nor let the owning node delete it.
    /// </summary>
    /// <remarks>
    /// <para>GH-4645 made this delete the inbox row at all, which fixed the orphan. It left the send
    /// first, and that is still two losses:</para>
    ///
    /// <para><b>The probe window.</b> Before every durable pop, the owning node's listener deletes any
    /// queue row whose id is already in the inbox at that queue's address (GH-4316), with no status
    /// filter. A forwarded envelope parks in the inbox under its eventual destination, so between the
    /// send's queue insert and this delete, that holds for the message we just forwarded: the owner
    /// deletes the queue row on its next poll and nothing is handled, logged or dead lettered.</para>
    ///
    /// <para><b>The failed first send.</b> EnqueueOutgoingAsync posts to the agent's RetryBlock and
    /// stores nothing. RetryBlock.PostAsync awaits the first attempt and, on an exception, queues the
    /// item and returns — so the inbox row was deleted anyway and, until a retry succeeds, no table holds
    /// the message at all. A process that stops in that interval loses it.</para>
    ///
    /// <para>So for a durable agent: store the outgoing row, delete the inbox row, then send. The probe
    /// has nothing left to match, a failed send is the ordinary outbox case the agent already retries
    /// from, and outbox recovery picks it up if this node stops. A stop between the store and the delete
    /// leaves BOTH rows, which can deliver twice and cannot lose.</para>
    ///
    /// <para>An agent with no outbox behind it answers false and keeps the original order: there is no
    /// durable home to move the message into, so reordering would only widen the window in which neither
    /// table holds it.</para>
    /// </remarks>
    /// <param name="parkedAt">
    /// The address the inbox row was written under, when that is not the envelope's current destination.
    /// See <see cref="retireForwardedInboxRowAsync"/>.
    /// </param>
    // Internal rather than private so CoreTests can assert the ORDER of the three operations directly.
    // Reaching this through EnqueueDirectlyAsync would need a real durable agent for an external
    // destination, and the thing worth pinning down is three statements long.
    internal async Task HandOverAsync(ISendingAgent sender, Envelope envelope, ISet<Envelope> handedOver,
        Uri? parkedAt = null)
    {
        if (await sender.TryStoreOutgoingAsync(envelope))
        {
            // Marked handed over as soon as the outbox row exists, NOT once the send is away: from here on
            // releasing the inbox row back to AnyNode would let recovery deliver the message a second time.
            handedOver.Add(envelope);

            await retireForwardedInboxRowAsync(envelope, parkedAt);
            await sender.EnqueueOutgoingAsync(envelope);

            return;
        }

        await sender.EnqueueOutgoingAsync(envelope);
        handedOver.Add(envelope);
        await retireForwardedInboxRowAsync(envelope, parkedAt);
    }

    /// <summary>
    /// GH-4822. The release matches on id AND <c>received_at</c>, so it has to name the address the rows were
    /// parked under. The slot forward has already re-addressed every envelope it touched to the slot, so
    /// stand-ins carry the parked address rather than mutating the live envelopes back -- the same reasoning
    /// as <see cref="retireForwardedInboxRowAsync"/>.
    /// </summary>
    private async Task releaseToAnyNodeAsync(IReadOnlyList<Envelope> envelopes, Uri parkedAt)
    {
        if (envelopes.Count == 0) return;

        foreach (var byStore in envelopes.GroupBy(x => x.Store ?? Storage))
        {
            try
            {
                var released = byStore
                    .Select(x => new Envelope { Id = x.Id, Destination = parkedAt, Store = x.Store })
                    .ToArray();

                await byStore.Key.ReassignIncomingAsync(TransportConstants.AnyNode, released);
            }
            catch (Exception e)
            {
                // Deliberately not rethrowing, for the same reason retireForwardedInboxRowAsync does not: the
                // rest of the batch still deserves its turn, and a stranded row is recoverable by hand.
                Logger.LogError(e,
                    "Error trying to release {Count} un-enqueued promoted envelopes back to any node",
                    byStore.Count());
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
    private async Task forwardToPartitionSlotAsync(IEnumerable<Envelope> group, Uri slotUri,
        ISet<Envelope> handedOver)
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

            // GH-4824. Same ordering as the sender branch above, and for the same two reasons -- this call
            // site ran the identical send-then-delete pair. The destination is already rewritten, so the
            // outbox row is written against the slot, which is where a recovered envelope has to go.
            await HandOverAsync(sender, envelope, handedOver, parkedAt);
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
