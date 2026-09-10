using System.Collections.Concurrent;
using ImTools;

namespace Wolverine.Runtime.Batching;

/// <summary>
/// Tracks how many member envelopes are somewhere inside a message-batching pipeline: posted into a
/// <see cref="BatchingProcessor{T}"/>'s batching channel, waiting in a grouped batch on the (deliberately
/// unbounded, GH-3287) local execution queue, or executing in a batch handler that hasn't reached its
/// terminal yet. It keeps two independent counts of the same members:
/// <list type="bullet">
/// <item><see cref="PendingFor"/> — per originating <b>listener address</b>, for back-pressure (CritterWatch#942).
/// Only members that arrived through a transport listener are counted.</item>
/// <item><see cref="PendingForBatchedMessage{T}"/> / <see cref="TotalPendingBatchMembers"/> — per
/// <b>batching pipeline</b>, counting every member however it arrived, local publishes and cascades
/// included (GH-4397). Use these to ask "is a batch of T still pending or executing?".</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Without the listener count, <c>BatchMessagesOf</c> severs the back-pressure chain: the transport
/// listener's bounded receive block — the only stage <see cref="Transports.BackPressureAgent"/> watches —
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
/// (local sends and cascades) are not counted there. That is exactly why the listener count cannot
/// answer "is a batch still in flight" for in-process publishes, and why the pipeline count exists.
/// </para>
/// <para>
/// Both counts increment per member in <see cref="BatchingProcessor{T}.HandleAsync"/>; settlement
/// happens once per grouped batch envelope at its terminal — the <c>CompleteAsync</c> batch
/// branches in <c>BufferedReceiver</c>/<c>DurableReceiver</c> — guarded by
/// <c>Envelope.BatchPendingSettled</c> so a double-complete can't drive either count negative. The
/// pipeline count only releases batches marked <c>Envelope.BatchPipelineCounted</c> by whoever counted
/// them, because a reduced batch replayed by <c>ApplyItemException</c> carries new member envelopes the
/// original batch's terminal has already released.
/// </para>
/// </remarks>
public class BatchingPendingCounts
{
    private readonly ConcurrentDictionary<Uri, long> _pending = new();

    // GH-4397. Registered once per BatchingProcessor at bootstrap and read on every batch terminal, so
    // ImHashMap: lock-free, non-allocating lookups, copy-on-write registration under the lock.
    private readonly object _registrationLock = new();
    private ImHashMap<string, BatchPipelineCounter> _pipelinesByBatchType = ImHashMap<string, BatchPipelineCounter>.Empty;
    private ImHashMap<Type, BatchPipelineCounter> _pipelinesByElementType = ImHashMap<Type, BatchPipelineCounter>.Empty;

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

        if (batchEnvelope.BatchPipelineCounted && findPipeline(batchEnvelope) is { } pipeline)
        {
            pipeline.Release(batchEnvelope.Batch.Length);
        }
    }

    /// <summary>
    /// How many member envelopes received at this <b>listener</b> address are still pending inside a
    /// batching pipeline. Members published in-process (local sends and cascades) have no listener and are
    /// never counted here — this is the back-pressure view. To ask whether a batch is still in flight
    /// regardless of where its members came from, use <see cref="PendingForBatchedMessage{T}"/>.
    /// </summary>
    public int PendingFor(Uri listenerAddress)
    {
        if (!_pending.TryGetValue(listenerAddress, out var count)) return 0;
        return clamp(count);
    }

    /// <summary>
    /// How many members of the <c>BatchMessagesOf&lt;T&gt;()</c> pipeline for element type
    /// <typeparamref name="T"/> are still waiting to be batched, waiting as an assembled batch, or executing
    /// in the batch handler — however they arrived. Zero means every batch that pipeline was given has
    /// reached its terminal.
    /// </summary>
    public int PendingForBatchedMessage<T>() => PendingForBatchedMessage(typeof(T));

    /// <inheritdoc cref="PendingForBatchedMessage{T}"/>
    public int PendingForBatchedMessage(Type elementType)
    {
        return _pipelinesByElementType.TryFind(elementType, out var pipeline) ? clamp(pipeline.Count) : 0;
    }

    /// <summary>
    /// How many members are pending across every batching pipeline in this application, however they
    /// arrived. Zero means no batch anywhere is waiting or executing.
    /// </summary>
    public int TotalPendingBatchMembers
    {
        get
        {
            long total = 0;
            foreach (var entry in _pipelinesByBatchType.Enumerate())
            {
                total += entry.Value.Count;
            }

            return clamp(total);
        }
    }

    /// <summary>
    /// Get or create the counter for one batching pipeline. Idempotent, because a BatchingProcessor can be
    /// built more than once under a startup race and every build must share the one count.
    /// </summary>
    internal BatchPipelineCounter RegisterPipeline(Type elementType, string batchMessageTypeName)
    {
        lock (_registrationLock)
        {
            if (!_pipelinesByBatchType.TryFind(batchMessageTypeName, out var pipeline))
            {
                pipeline = new BatchPipelineCounter();
                _pipelinesByBatchType = _pipelinesByBatchType.AddOrUpdate(batchMessageTypeName, pipeline);
            }

            if (!_pipelinesByElementType.TryFind(elementType, out _))
            {
                _pipelinesByElementType = _pipelinesByElementType.AddOrUpdate(elementType, pipeline);
            }

            return pipeline;
        }
    }

    /// <summary>
    /// Count a reduced batch replayed onto its pipeline's queue (<c>ApplyItemException</c>,
    /// <c>IsolateBatchMembers</c>) as pending until its own terminal settles it.
    /// </summary>
    internal void CountReplayedBatch(Envelope reducedBatch)
    {
        if (reducedBatch.Batch == null || findPipeline(reducedBatch) is not { } pipeline) return;

        pipeline.Add(reducedBatch.Batch.Length);
        reducedBatch.BatchPipelineCounted = true;
    }

    private BatchPipelineCounter? findPipeline(Envelope batchEnvelope)
    {
        return batchEnvelope.MessageType != null &&
               _pipelinesByBatchType.TryFind(batchEnvelope.MessageType, out var pipeline)
            ? pipeline
            : null;
    }

    private static int clamp(long count) => count > int.MaxValue ? int.MaxValue : (int)count;
}

/// <summary>
/// GH-4397 — one batching pipeline's count of pending members. Held directly by its
/// <see cref="BatchingProcessor{T}"/> so the per-member increment is a single interlocked add.
/// </summary>
internal sealed class BatchPipelineCounter
{
    private long _count;

    public long Count => Interlocked.Read(ref _count);

    public void Increment() => Interlocked.Increment(ref _count);

    public void Decrement() => Release(1);

    public void Add(int members) => Interlocked.Add(ref _count, members);

    /// <summary>Subtract, clamping at zero so a release for members never counted can't wedge it below reality.</summary>
    public void Release(int members)
    {
        var current = Interlocked.Read(ref _count);
        while (true)
        {
            var next = current > members ? current - members : 0;
            var observed = Interlocked.CompareExchange(ref _count, next, current);
            if (observed == current) return;

            current = observed;
        }
    }
}
