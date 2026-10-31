using System.Collections.Concurrent;
using System.Diagnostics;
using Dekaf;
using Dekaf.Consumer;
using Microsoft.Extensions.Logging;

namespace Wolverine.Kafka.Internals;

/// <summary>
/// Encapsulates the offset-commit strategy for a single Kafka consumer (GH-3150). Commits the
/// *specific* TopicPartitionOffset of each completed message (never the consumer's global position)
/// and respects the user's OffsetCommitMode/EnableAutoOffsetStore configuration.
///
/// All three manual strategies (PerMessage, Store, Batch) route through a per-partition
/// <see cref="OffsetWatermark"/> so that under concurrent, out-of-order handler completion the
/// committed/stored position can never move past an offset that is still in flight (GH-3161). The
/// watermark is seeded by <see cref="Track"/> at consume time (offsets arrive in order per partition)
/// and advanced as each message finishes; the committable position is the lowest still-in-flight
/// delivered offset, or the high-water consumed offset + 1 when nothing is in flight, which also
/// tolerates the offset gaps a compacted or transactional (read_committed) topic produces.
///
/// Handlers only queue their completions through <see cref="Complete"/>. The consume loop applies them and
/// commits or stores offsets in <see cref="CommitCompletedAsync"/>, so handler threads never call the consumer.
/// Revocations arrive on Dekaf's rebalance task; they apply the queue themselves and share one commit gate with
/// the loop, so an offset is never taken by one and committed after the other has handed the partition off.
/// </summary>
internal sealed class KafkaOffsetCommitter
{
    internal enum Strategy
    {
        /// User opted into Kafka's own auto-commit; Wolverine issues no offset calls.
        Automatic,

        /// Store each completed offset; Kafka's background committer flushes it.
        Store,

        /// Commit completed offsets on every pass of the consume loop.
        PerMessage,

        /// Commit the contiguous watermark after N messages / T elapsed.
        Batch
    }

    private readonly IKafkaConsumer<string, byte[]> _consumer;
    private readonly ILogger _logger;
    private readonly CommitMode _mode;
    private readonly int _batchCount;
    private readonly TimeSpan _batchInterval;
    private readonly Strategy _strategy;

    // Completed offsets, queued by handler threads until the loop or a revocation applies them
    private readonly ConcurrentQueue<TopicPartitionOffset> _completed = new();

    // Held from taking a committable offset until its commit or store returns
    private readonly SemaphoreSlim _commitGate = new(1, 1);

    private readonly object _lock = new();
    private readonly Dictionary<TopicPartition, OffsetWatermark> _watermarks = new();

    // Partitions taken away by a rebalance, until this consumer is delivered one of their records again. A completion
    // that lands in between is not committed: the partition's new owner reads from the committed position, and a
    // late commit could move it.
    private readonly HashSet<TopicPartition> _revoked = new();
    private int _sinceLastCommit;
    private long _lastCommitTimestamp;

    public KafkaOffsetCommitter(IKafkaConsumer<string, byte[]> consumer, KafkaConsumerOptions config, CommitMode mode,
        int batchCount, TimeSpan batchInterval, ILogger logger)
    {
        _consumer = consumer;
        _logger = logger;
        _mode = mode;
        _batchCount = batchCount < 1 ? 1 : batchCount;
        _batchInterval = batchInterval;
        _strategy = ResolveStrategy(config, mode);
        _lastCommitTimestamp = Stopwatch.GetTimestamp();
    }

    internal Strategy ResolvedStrategy => _strategy;

    /// <summary>
    /// Determine the effective commit behavior by reconciling the requested <see cref="CommitMode"/>
    /// with the consumer configuration. An explicit <c>OffsetCommitMode.Auto</c> always suppresses
    /// Wolverine's manual commits (GH-3150 acceptance #1).
    /// </summary>
    internal static Strategy ResolveStrategy(KafkaConsumerOptions config, CommitMode mode)
    {
        if (config.OffsetCommitMode == OffsetCommitMode.Auto)
        {
            // The user (or StoreThenAutoFlush wiring) turned on Kafka's background committer. If offset
            // storage is manual we still store each completed offset; otherwise stay fully hands-off.
            return config.EnableAutoOffsetStore == false ? Strategy.Store : Strategy.Automatic;
        }

        return mode switch
        {
            CommitMode.PerMessage => Strategy.PerMessage,
            CommitMode.BatchCount => Strategy.Batch,
            CommitMode.BatchInterval => Strategy.Batch,
            // StoreThenAutoFlush requires OffsetCommitMode.Auto to flush; if that wasn't wired the
            // stored offsets would never flush, so fall back to per-message commits rather than silently
            // never committing.
            _ => Strategy.PerMessage
        };
    }

    /// <summary>
    /// Mutate <paramref name="config"/> so the Kafka client is wired for the requested mode. Called
    /// once at listener-build time. Leaves an explicit user OffsetCommitMode.Auto choice untouched.
    /// </summary>
    internal static void ApplyTo(KafkaConsumerOptions config, CommitMode mode)
    {
        if (config.OffsetCommitMode == OffsetCommitMode.Auto)
        {
            return; // respect the user's explicit auto-commit choice
        }

        switch (mode)
        {
            case CommitMode.StoreThenAutoFlush:
                config.OffsetCommitMode = OffsetCommitMode.Auto;
                config.EnableAutoOffsetStore = false;
                break;

            case CommitMode.PerMessage:
            case CommitMode.BatchCount:
            case CommitMode.BatchInterval:
                config.OffsetCommitMode = OffsetCommitMode.Manual;
                break;
        }
    }

    /// <summary>
    /// Record that a message has been *delivered* to a handler (read off the partition, in order) so the
    /// watermark knows it is in flight. Called from the consume loop before dispatch so the committable
    /// position can never advance past a delivered-but-incomplete offset, even when an earlier message
    /// finishes after a later one (GH-3161).
    /// </summary>
    public void Track(string topic, int partition, long offset)
    {
        if (_strategy == Strategy.Automatic)
        {
            return;
        }

        var tp = new TopicPartition(topic, partition);
        lock (_lock)
        {
            _revoked.Remove(tp);
            watermarkFor(tp).Track(offset);
        }
    }

    /// <summary>
    /// Record that the message at these coordinates finished. Safe from any thread: the offset is committed or
    /// stored by the next <see cref="CommitCompletedAsync"/> or revocation.
    /// </summary>
    public void Complete(string topic, int partition, long offset)
    {
        if (_strategy != Strategy.Automatic)
        {
            _completed.Enqueue(new TopicPartitionOffset(topic, partition, offset));
        }
    }

    /// <summary>
    /// Applies the queued completions and commits or stores what the strategy calls for. Called by the consume loop
    /// before each consume, so only the loop thread commits during normal operation.
    /// </summary>
    public ValueTask CommitCompletedAsync()
    {
        if (_strategy == Strategy.Automatic || (_completed.IsEmpty && !batchIntervalElapsed()))
        {
            return ValueTask.CompletedTask;
        }

        return commitCompletedAsync(force: false);
    }

    /// <summary>
    /// Commit or store everything completed so far. Called on graceful shutdown, after the consume loop has stopped
    /// and before the consumer closes, so a clean stop doesn't lose progress (GH-3150 acceptance #4).
    /// </summary>
    public ValueTask FlushAsync()
    {
        return _strategy == Strategy.Automatic ? ValueTask.CompletedTask : commitCompletedAsync(force: true);
    }

    /// <summary>
    /// The consumer group is taking these partitions away. Commits what has finished on them while this consumer
    /// still owns them, so the next owner does not process it again, then stops tracking them.
    /// </summary>
    public async ValueTask PartitionsRevokedAsync(IEnumerable<TopicPartition> partitions)
    {
        if (_strategy == Strategy.Automatic)
        {
            return;
        }

        await _commitGate.WaitAsync();
        try
        {
            List<TopicPartitionOffset>? toCommit = null;
            lock (_lock)
            {
                applyCompleted();

                foreach (var tp in partitions)
                {
                    // Dekaf has already committed the stored offsets, so a Store watermark only has what finished
                    // since its last store
                    if (_watermarks.Remove(tp, out var watermark) && watermark.TryTakeCommittable(out var nextOffset))
                    {
                        (toCommit ??= []).Add(new TopicPartitionOffset(tp.Topic, tp.Partition, nextOffset));
                    }

                    _revoked.Add(tp);
                }
            }

            if (toCommit != null)
            {
                await commitOffsetsAsync(toCommit);
            }
        }
        finally
        {
            _commitGate.Release();
        }
    }

    /// <summary>
    /// The consumer lost these partitions without a revocation (its group membership expired), so a commit would be
    /// rejected. Stops tracking them.
    /// </summary>
    public void PartitionsLost(IEnumerable<TopicPartition> partitions)
    {
        lock (_lock)
        {
            foreach (var tp in partitions)
            {
                _watermarks.Remove(tp);
                _revoked.Add(tp);
            }
        }
    }

    private async ValueTask commitCompletedAsync(bool force)
    {
        await _commitGate.WaitAsync();
        try
        {
            List<TopicPartitionOffset>? taken = null;
            lock (_lock)
            {
                applyCompleted();

                if (_strategy != Strategy.Batch || force || shouldCommitBatch())
                {
                    taken = takeCommittable();
                    _sinceLastCommit = 0;
                    _lastCommitTimestamp = Stopwatch.GetTimestamp();
                }
            }

            if (taken is not { Count: > 0 })
            {
                return;
            }

            if (_strategy == Strategy.Store)
            {
                foreach (var offset in taken) storeOffset(offset);
            }
            else
            {
                await commitOffsetsAsync(taken);
            }
        }
        finally
        {
            _commitGate.Release();
        }
    }

    // Called under _lock
    private void applyCompleted()
    {
        while (_completed.TryDequeue(out var completed))
        {
            var tp = new TopicPartition(completed.Topic, completed.Partition);
            if (_revoked.Contains(tp))
            {
                continue;
            }

            watermarkFor(tp).Complete(completed.Offset);
            _sinceLastCommit++;
        }
    }

    private OffsetWatermark watermarkFor(TopicPartition tp)
    {
        if (!_watermarks.TryGetValue(tp, out var watermark))
        {
            watermark = new OffsetWatermark();
            _watermarks[tp] = watermark;
        }

        return watermark;
    }

    private bool shouldCommitBatch()
    {
        if (_mode == CommitMode.BatchCount)
        {
            return _sinceLastCommit >= _batchCount;
        }

        // BatchInterval: commit once at least the interval has elapsed since the last commit
        return Stopwatch.GetElapsedTime(_lastCommitTimestamp) >= _batchInterval;
    }

    // Lets the loop commit a BatchInterval watermark once the interval passes, even with no new completions
    private bool batchIntervalElapsed()
    {
        return _strategy == Strategy.Batch && _mode != CommitMode.BatchCount && Volatile.Read(ref _sinceLastCommit) > 0
               && Stopwatch.GetElapsedTime(Volatile.Read(ref _lastCommitTimestamp)) >= _batchInterval;
    }

    private List<TopicPartitionOffset> takeCommittable()
    {
        var list = new List<TopicPartitionOffset>();
        foreach (var (tp, watermark) in _watermarks)
        {
            if (watermark.TryTakeCommittable(out var nextOffset))
            {
                list.Add(new TopicPartitionOffset(tp.Topic, tp.Partition, nextOffset));
            }
        }

        return list;
    }

    private async ValueTask commitOffsetsAsync(List<TopicPartitionOffset> offsets)
    {
        try
        {
            await _consumer.CommitAsync(offsets, CancellationToken.None);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Error committing Kafka offsets {Offsets}", offsets);
        }
    }

    private void storeOffset(TopicPartitionOffset offset)
    {
        try
        {
            _consumer.StoreOffset(offset);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Error storing Kafka offset {Offset}", offset);
        }
    }

    /// <summary>
    /// Tracks the safe commit position for a single partition so the committed offset never moves past
    /// an offset that is still in flight, even under concurrent out-of-order completion (GH-3161).
    ///
    /// <para><see cref="Track"/> records each delivered offset (consume order) as in-flight;
    /// <see cref="Complete"/> clears it on success. The committable position is the lowest still
    /// in-flight delivered offset, or the high-water consumed offset + 1 once nothing is in flight. This
    /// makes no assumption that offsets are gap-free, so it also behaves correctly on compacted or
    /// transactional (read_committed) topics where the broker hands out non-contiguous offsets, and it
    /// self-heals across a rebalance because the in-flight set drains as handlers finish.</para>
    ///
    /// <para>The reported position is monotonic: a re-seek that re-delivers an already-passed offset
    /// will not move the committed position backwards.</para>
    /// </summary>
    internal sealed class OffsetWatermark
    {
        private readonly SortedSet<long> _inflight = new(); // delivered but not yet completed
        private long _highWater = -1; // highest delivered offset seen, or -1 before anything is seen
        private bool _seen;
        private bool _tracked; // whether any offset has been delivered through Track
        private long _lastCommittable = -1; // last next-offset reported, to keep the position monotonic

        public void Track(long offset)
        {
            _tracked = true;
            _inflight.Add(offset);
            Observe(offset);
        }

        public void Complete(long offset)
        {
            // Once records are tracked, a completion of an offset this watermark never delivered comes from before a
            // rebalance handed the partition back, and must not move the position past records delivered since
            if (!_inflight.Remove(offset) && _tracked)
            {
                return;
            }

            Observe(offset);
        }

        private void Observe(long offset)
        {
            if (!_seen)
            {
                _highWater = offset;
                // Seed the baseline resume position at the first delivered offset so we only ever
                // commit/store once progress advances past it — never a redundant baseline write.
                _lastCommittable = offset;
                _seen = true;
                return;
            }

            if (offset > _highWater)
            {
                _highWater = offset;
            }
        }

        /// <summary>
        /// If the safe commit position has advanced since the last take, return the next offset to
        /// commit/store (the lowest in-flight offset, or high-water + 1 when nothing is in flight).
        /// </summary>
        public bool TryTakeCommittable(out long nextOffset)
        {
            if (!_seen)
            {
                nextOffset = default;
                return false;
            }

            var candidate = _inflight.Count == 0 ? _highWater + 1 : _inflight.Min;
            if (candidate > _lastCommittable)
            {
                _lastCommittable = candidate;
                nextOffset = candidate;
                return true;
            }

            nextOffset = default;
            return false;
        }
    }
}
