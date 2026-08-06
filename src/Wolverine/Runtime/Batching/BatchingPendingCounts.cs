using System.Collections.Concurrent;

namespace Wolverine.Runtime.Batching;

/// <summary>
/// CritterWatch#942 — tracks, per originating listener address, how many member envelopes are
/// somewhere inside a message-batching pipeline: posted into a <see cref="BatchingProcessor{T}"/>'s
/// batching channel, waiting in a grouped batch on the (deliberately unbounded, GH-3287) local
/// execution queue, or executing in a batch handler that hasn't reached its terminal yet.
/// </summary>
/// <remarks>
/// <para>
/// Without this, <c>BatchMessagesOf</c> severs the back-pressure chain: the transport listener's
/// bounded receive block — the only stage <see cref="Transports.BackPressureAgent"/> watches —
/// always drains instantly into the batching channel and onward to the unbounded local queue, so
/// <c>QueueCount</c> reads near zero while the real backlog (with every member's payload and
/// deserialized message graph pinned via <see cref="Envelope.Batch"/>) grows without bound. That is
/// the mechanism behind the CritterWatch#942 field OOM: 2.7 → 6 GiB in two minutes, queue "empty".
/// </para>
/// <para>
/// <see cref="Transports.ListeningAgent.QueueCount"/> adds <see cref="PendingFor"/> to the receive
/// block's own depth, so the existing back-pressure latch pauses the external listener when the
/// batching pipeline is deep — restoring the bounded-memory behavior per-message handling had,
/// without bounding any local queue (which would deadlock self-cascading handlers, GH-3287) and
/// without ever blocking a local publisher: envelopes with no <see cref="Envelope.Listener"/>
/// (local sends and cascades) are not counted.
/// </para>
/// <para>
/// Increment happens per member in <see cref="BatchingProcessor{T}.HandleAsync"/>; settlement
/// happens once per grouped batch envelope at its terminal — the <c>CompleteAsync</c> batch
/// branches in <c>BufferedReceiver</c>/<c>DurableReceiver</c> — guarded by
/// <c>Envelope.BatchPendingSettled</c> so a double-complete can't drive the count negative.
/// </para>
/// </remarks>
public class BatchingPendingCounts
{
    private readonly ConcurrentDictionary<Uri, long> _pending = new();

    public void Increment(Uri? listenerAddress)
    {
        if (listenerAddress == null) return;
        _pending.AddOrUpdate(listenerAddress, 1, static (_, current) => current + 1);
    }

    public void Decrement(Uri? listenerAddress)
    {
        if (listenerAddress == null) return;
        // Clamp at zero: a decrement for an address we never counted (or after a reset) must not
        // wedge the listener's QueueCount below reality.
        _pending.AddOrUpdate(listenerAddress, 0, static (_, current) => current > 0 ? current - 1 : 0);
    }

    /// <summary>
    /// Settle every member of a grouped batch envelope exactly once. Safe to call from any terminal
    /// (success, dead-letter, discard) — the envelope-level flag makes repeats a no-op.
    /// </summary>
    public void SettleBatch(Envelope batchEnvelope)
    {
        if (batchEnvelope.Batch == null || batchEnvelope.BatchPendingSettled) return;
        batchEnvelope.BatchPendingSettled = true;

        foreach (var member in batchEnvelope.Batch)
        {
            Decrement(member.Listener?.Address);
        }
    }

    /// <summary>How many member envelopes received at this listener address are still pending inside a batching pipeline.</summary>
    public int PendingFor(Uri listenerAddress)
    {
        if (!_pending.TryGetValue(listenerAddress, out var count)) return 0;
        return count > int.MaxValue ? int.MaxValue : (int)count;
    }
}
