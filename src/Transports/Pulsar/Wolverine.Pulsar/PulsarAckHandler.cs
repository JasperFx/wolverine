using System.Buffers;
using DotPulsar;
using DotPulsar.Abstractions;
using DotPulsar.Extensions;

namespace Wolverine.Pulsar;

/// <summary>
/// Encapsulates how a Pulsar consumer acknowledges completed messages for a given
/// <see cref="PulsarAckStrategy"/>. See #3180.
///
/// The cumulative strategy is the only one with an ordering hazard: under the buffered listener,
/// messages complete out of order, and a naive cumulative ack of a later message would confirm the
/// earlier, still-in-flight ones (silent message loss). This handler tracks in-flight message ids and
/// only ever advances the cumulative ack to the highest <em>contiguous</em> completed message — i.e.
/// the largest completed id with no smaller id still in flight — so it never acks past in-flight work.
/// </summary>
internal sealed class PulsarAckHandler : IAsyncDisposable
{
    internal static readonly IComparer<MessageId> Comparer = new MessageIdComparer();

    private readonly IConsumer<ReadOnlySequence<byte>> _consumer;
    private readonly PulsarAckStrategy _strategy;
    private readonly int _batchSize;
    private readonly CancellationToken _cancellation;
    private readonly object _lock = new();

    // Batched
    private readonly List<MessageId> _pendingBatch = new();
    private readonly Timer? _batchTimer;

    // Cumulative
    private readonly SortedSet<MessageId> _inFlight = new(Comparer);
    private readonly SortedSet<MessageId> _completed = new(Comparer);

    public PulsarAckHandler(IConsumer<ReadOnlySequence<byte>> consumer, PulsarAckStrategy strategy, int batchSize,
        TimeSpan batchInterval, CancellationToken cancellation)
    {
        _consumer = consumer;
        _strategy = strategy;
        _batchSize = batchSize < 1 ? 1 : batchSize;
        _cancellation = cancellation;

        if (_strategy == PulsarAckStrategy.Batched && batchInterval > TimeSpan.Zero)
        {
            _batchTimer = new Timer(_ => _ = flushBatchOnTimerAsync(), null, batchInterval, batchInterval);
        }
    }

    /// <summary>
    /// Record a message as received/in-flight. Only the cumulative strategy needs this so it can
    /// compute the contiguous-completed watermark.
    /// </summary>
    public void Track(MessageId messageId)
    {
        if (_strategy != PulsarAckStrategy.Cumulative)
        {
            return;
        }

        lock (_lock)
        {
            _inFlight.Add(messageId);
        }
    }

    public async ValueTask CompleteAsync(MessageId messageId)
    {
        switch (_strategy)
        {
            case PulsarAckStrategy.Individual:
                await _consumer.Acknowledge(messageId, _cancellation);
                break;

            case PulsarAckStrategy.Batched:
                List<MessageId>? toFlush = null;
                lock (_lock)
                {
                    _pendingBatch.Add(messageId);
                    if (_pendingBatch.Count >= _batchSize)
                    {
                        toFlush = [.. _pendingBatch];
                        _pendingBatch.Clear();
                    }
                }

                if (toFlush != null)
                {
                    await _consumer.Acknowledge(toFlush, _cancellation);
                }

                break;

            case PulsarAckStrategy.Cumulative:
                MessageId? watermark;
                lock (_lock)
                {
                    _inFlight.Remove(messageId);
                    _completed.Add(messageId);
                    watermark = takeCumulativeWatermark();
                }

                if (watermark is not null)
                {
                    await _consumer.AcknowledgeCumulative(watermark, _cancellation);
                }

                break;
        }
    }

    /// <summary>
    /// The highest completed id that is strictly below the smallest still-in-flight id (or the highest
    /// completed id if nothing is in flight). Completed ids up to the watermark are removed from
    /// tracking because the single cumulative ack covers them all. Caller must hold <see cref="_lock"/>.
    /// </summary>
    private MessageId? takeCumulativeWatermark()
    {
        MessageId? floor = null;
        if (_inFlight.Count > 0)
        {
            floor = _inFlight.Min;
        }

        MessageId? watermark = null;
        foreach (var completed in _completed)
        {
            if (floor is not null && Comparer.Compare(completed, floor) >= 0)
            {
                break;
            }

            watermark = completed;
        }

        if (watermark is not null)
        {
            _completed.RemoveWhere(x => Comparer.Compare(x, watermark) <= 0);
        }

        return watermark;
    }

    private async Task flushBatchOnTimerAsync()
    {
        try
        {
            List<MessageId>? toFlush = null;
            lock (_lock)
            {
                if (_pendingBatch.Count > 0)
                {
                    toFlush = [.. _pendingBatch];
                    _pendingBatch.Clear();
                }
            }

            if (toFlush != null)
            {
                await _consumer.Acknowledge(toFlush, _cancellation);
            }
        }
        catch
        {
            // Best-effort periodic flush; the next flush (or disposal) will retry remaining acks.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_batchTimer != null)
        {
            await _batchTimer.DisposeAsync();
        }

        // Flush any remaining batched acks on shutdown.
        if (_strategy == PulsarAckStrategy.Batched)
        {
            List<MessageId>? toFlush = null;
            lock (_lock)
            {
                if (_pendingBatch.Count > 0)
                {
                    toFlush = [.. _pendingBatch];
                    _pendingBatch.Clear();
                }
            }

            if (toFlush != null)
            {
                try
                {
                    await _consumer.Acknowledge(toFlush, _cancellation);
                }
                catch
                {
                    // Shutting down — unacked messages will simply be redelivered.
                }
            }
        }
    }

    private sealed class MessageIdComparer : IComparer<MessageId>
    {
        public int Compare(MessageId? x, MessageId? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var c = x.LedgerId.CompareTo(y.LedgerId);
            if (c != 0) return c;
            c = x.EntryId.CompareTo(y.EntryId);
            if (c != 0) return c;
            c = x.Partition.CompareTo(y.Partition);
            if (c != 0) return c;
            return x.BatchIndex.CompareTo(y.BatchIndex);
        }
    }
}
