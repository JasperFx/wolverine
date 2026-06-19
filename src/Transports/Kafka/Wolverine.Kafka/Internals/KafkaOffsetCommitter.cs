using System.Diagnostics;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace Wolverine.Kafka.Internals;

/// <summary>
/// Encapsulates the offset-commit strategy for a single Kafka consumer (GH-3150). Shared by the
/// single-topic and grouped listeners. Commits the *specific* TopicPartitionOffset of each completed
/// message (never the consumer's global position) and respects the user's
/// EnableAutoCommit/EnableAutoOffsetStore configuration.
///
/// All three manual strategies (PerMessage, Store, Batch) route through a per-partition
/// <see cref="OffsetWatermark"/> so that under concurrent, out-of-order handler completion the
/// committed/stored position can never move past an offset that is still in flight (GH-3161). The
/// watermark is seeded by <see cref="Track"/> at consume time (offsets arrive in order per partition)
/// and advanced by <see cref="Complete"/> as each message finishes; the committable position is the
/// lowest still-in-flight delivered offset, or the high-water consumed offset + 1 when nothing is in
/// flight — which also tolerates the offset gaps a compacted or transactional (read_committed) topic
/// produces.
/// </summary>
internal sealed class KafkaOffsetCommitter
{
    internal enum Strategy
    {
        /// User opted into Kafka's own auto-commit; Wolverine issues no offset calls.
        Automatic,

        /// Store each completed offset; Kafka's background committer flushes it.
        Store,

        /// Synchronously commit each completed offset.
        PerMessage,

        /// Commit the contiguous watermark after N messages / T elapsed.
        Batch
    }

    private readonly IConsumer<string, byte[]> _consumer;
    private readonly ILogger _logger;
    private readonly CommitMode _mode;
    private readonly int _batchCount;
    private readonly TimeSpan _batchInterval;
    private readonly Strategy _strategy;

    private readonly object _lock = new();
    private readonly Dictionary<TopicPartition, OffsetWatermark> _watermarks = new();
    private int _sinceLastCommit;
    private long _lastCommitTimestamp;

    public KafkaOffsetCommitter(IConsumer<string, byte[]> consumer, ConsumerConfig config, CommitMode mode,
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
    /// with the consumer configuration. An explicit <c>EnableAutoCommit=true</c> always suppresses
    /// Wolverine's manual commits (GH-3150 acceptance #1).
    /// </summary>
    internal static Strategy ResolveStrategy(ConsumerConfig config, CommitMode mode)
    {
        if (config.EnableAutoCommit == true)
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
            // StoreThenAutoFlush requires EnableAutoCommit=true to flush; if that wasn't wired the
            // stored offsets would never flush, so fall back to per-message commits rather than silently
            // never committing.
            _ => Strategy.PerMessage
        };
    }

    /// <summary>
    /// Mutate <paramref name="config"/> so the Kafka client is wired for the requested mode. Called
    /// once at listener-build time. Leaves an explicit user EnableAutoCommit choice untouched.
    /// </summary>
    internal static void ApplyTo(ConsumerConfig config, CommitMode mode)
    {
        if (config.EnableAutoCommit == true)
        {
            return; // respect the user's explicit auto-commit choice
        }

        switch (mode)
        {
            case CommitMode.StoreThenAutoFlush:
                config.EnableAutoCommit = true;
                config.EnableAutoOffsetStore = false;
                break;

            case CommitMode.PerMessage:
            case CommitMode.BatchCount:
            case CommitMode.BatchInterval:
                config.EnableAutoCommit = false;
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

        var tp = new TopicPartition(topic, new Partition(partition));
        lock (_lock)
        {
            WatermarkFor(tp).Track(offset);
        }
    }

    /// <summary>
    /// Record successful processing of a message at the given coordinates and commit/store as the
    /// strategy dictates. The committed offset is the lowest still-in-flight delivered offset (or the
    /// high-water consumed offset + 1 when nothing is in flight) — the next offset to resume from, per
    /// Kafka convention — so out-of-order completion never advances the position past in-flight work.
    /// </summary>
    public void Complete(string topic, int partition, long offset)
    {
        if (_strategy == Strategy.Automatic)
        {
            return;
        }

        var tp = new TopicPartition(topic, new Partition(partition));

        List<TopicPartitionOffset>? toCommit = null;
        List<TopicPartitionOffset>? toStore = null;
        lock (_lock)
        {
            var watermark = WatermarkFor(tp);
            watermark.Complete(offset);

            switch (_strategy)
            {
                case Strategy.PerMessage:
                    if (watermark.TryTakeCommittable(out var perMessage))
                    {
                        toCommit = [new TopicPartitionOffset(tp, new Offset(perMessage))];
                    }

                    break;

                case Strategy.Store:
                    if (watermark.TryTakeCommittable(out var store))
                    {
                        toStore = [new TopicPartitionOffset(tp, new Offset(store))];
                    }

                    break;

                case Strategy.Batch:
                    _sinceLastCommit++;
                    if (ShouldCommitBatch())
                    {
                        toCommit = PendingWatermarkOffsets();
                        _sinceLastCommit = 0;
                        _lastCommitTimestamp = Stopwatch.GetTimestamp();
                    }

                    break;
            }
        }

        if (toCommit is { Count: > 0 })
        {
            CommitOffsets(toCommit);
        }

        if (toStore is { Count: > 0 })
        {
            StoreOffsets(toStore);
        }
    }

    private OffsetWatermark WatermarkFor(TopicPartition tp)
    {
        if (!_watermarks.TryGetValue(tp, out var watermark))
        {
            watermark = new OffsetWatermark();
            _watermarks[tp] = watermark;
        }

        return watermark;
    }

    private bool ShouldCommitBatch()
    {
        if (_mode == CommitMode.BatchCount)
        {
            return _sinceLastCommit >= _batchCount;
        }

        // BatchInterval: commit once at least the interval has elapsed since the last commit. (Any
        // residual is flushed on shutdown, so an idle partition never strands progress permanently.)
        var elapsed = Stopwatch.GetElapsedTime(_lastCommitTimestamp);
        return elapsed >= _batchInterval;
    }

    private List<TopicPartitionOffset> PendingWatermarkOffsets()
    {
        var list = new List<TopicPartitionOffset>();
        foreach (var (tp, watermark) in _watermarks)
        {
            if (watermark.TryTakeCommittable(out var nextOffset))
            {
                list.Add(new TopicPartitionOffset(tp, new Offset(nextOffset)));
            }
        }

        return list;
    }

    /// <summary>
    /// Flush any pending offsets. Called on graceful shutdown so a clean stop doesn't lose progress
    /// (GH-3150 acceptance #4).
    /// </summary>
    public void Flush()
    {
        switch (_strategy)
        {
            case Strategy.Batch:
                List<TopicPartitionOffset> pending;
                lock (_lock)
                {
                    pending = PendingWatermarkOffsets();
                    _sinceLastCommit = 0;
                }

                if (pending.Count > 0)
                {
                    CommitOffsets(pending);
                }

                break;

            case Strategy.Store:
                // Force the stored offsets to be committed synchronously rather than waiting for the
                // next background flush (Close() also does this, but be explicit and resilient).
                try
                {
                    _consumer.Commit();
                }
                catch (KafkaException e) when (e.Error.Code == ErrorCode.Local_NoOffset)
                {
                    // Nothing stored yet — nothing to flush.
                }
                catch (Exception e)
                {
                    _logger.LogDebug(e, "Error flushing stored Kafka offsets on shutdown");
                }

                break;
        }
    }

    private void CommitOffsets(List<TopicPartitionOffset> offsets)
    {
        try
        {
            _consumer.Commit(offsets);
        }
        catch (KafkaException e) when (e.Error.Code == ErrorCode.Local_NoOffset)
        {
            // No offsets to commit.
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Error committing Kafka offsets {Offsets}", offsets);
        }
    }

    private void StoreOffsets(List<TopicPartitionOffset> offsets)
    {
        try
        {
            foreach (var tpo in offsets)
            {
                _consumer.StoreOffset(tpo);
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Error storing Kafka offsets {Offsets}", offsets);
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
        private long _lastCommittable = -1; // last next-offset reported, to keep the position monotonic

        public void Track(long offset)
        {
            _inflight.Add(offset);
            Observe(offset);
        }

        public void Complete(long offset)
        {
            _inflight.Remove(offset);
            // Tolerate a Complete without a prior Track (defensive) by still advancing the high water mark.
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
