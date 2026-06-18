using System.Diagnostics;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace Wolverine.Kafka.Internals;

/// <summary>
/// Encapsulates the offset-commit strategy for a single Kafka consumer (GH-3150). Shared by the
/// single-topic and grouped listeners. Commits the *specific* TopicPartitionOffset of each completed
/// message (never the consumer's global position), batches with a contiguous per-partition watermark
/// so it never commits ahead of the lowest in-flight offset, and respects the user's
/// EnableAutoCommit/EnableAutoOffsetStore configuration.
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
    /// Record successful processing of a message at the given coordinates and commit/store as the
    /// strategy dictates. The committed offset is always <paramref name="offset"/> + 1 (the next
    /// offset to resume from), per Kafka convention.
    /// </summary>
    public void Complete(string topic, int partition, long offset)
    {
        if (_strategy == Strategy.Automatic)
        {
            return;
        }

        var tp = new TopicPartition(topic, new Partition(partition));

        if (_strategy == Strategy.PerMessage)
        {
            CommitOffsets([new TopicPartitionOffset(tp, new Offset(offset + 1))]);
            return;
        }

        if (_strategy == Strategy.Store)
        {
            StoreOffsets([new TopicPartitionOffset(tp, new Offset(offset + 1))]);
            return;
        }

        // Strategy.Batch — advance the per-partition contiguous watermark and commit on threshold.
        List<TopicPartitionOffset>? toCommit = null;
        lock (_lock)
        {
            if (!_watermarks.TryGetValue(tp, out var watermark))
            {
                watermark = new OffsetWatermark();
                _watermarks[tp] = watermark;
            }

            watermark.Register(offset);
            _sinceLastCommit++;

            if (ShouldCommitBatch())
            {
                toCommit = PendingWatermarkOffsets();
                _sinceLastCommit = 0;
                _lastCommitTimestamp = Stopwatch.GetTimestamp();
            }
        }

        if (toCommit is { Count: > 0 })
        {
            CommitOffsets(toCommit);
        }
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
    /// Tracks the highest contiguous (gap-free) completed offset for a single partition so a batch
    /// commit never moves the committed position past an offset whose predecessors are still in
    /// flight. Offsets within a Kafka partition are contiguous, so a simple "advance while the next
    /// offset is present" loop yields the correct watermark even under out-of-order completion.
    /// </summary>
    internal sealed class OffsetWatermark
    {
        private long _contiguous = -1; // highest contiguous completed offset, or -1 before seeding
        private bool _seeded;
        private bool _dirty;
        private readonly SortedSet<long> _ahead = new();

        public void Register(long offset)
        {
            if (!_seeded)
            {
                _contiguous = offset - 1;
                _seeded = true;
            }

            if (offset <= _contiguous)
            {
                return; // already covered
            }

            _ahead.Add(offset);

            while (_ahead.Remove(_contiguous + 1))
            {
                _contiguous++;
                _dirty = true;
            }
        }

        /// <summary>
        /// If new contiguous progress has been made since the last take, return the offset to commit
        /// (contiguous + 1, i.e. the next offset to resume from) and reset the dirty flag.
        /// </summary>
        public bool TryTakeCommittable(out long nextOffset)
        {
            if (_dirty)
            {
                nextOffset = _contiguous + 1;
                _dirty = false;
                return true;
            }

            nextOffset = default;
            return false;
        }
    }
}
