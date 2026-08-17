using JasperFx.Core;

namespace Wolverine.SignalR.Internals;

/// <summary>
///     GH-3972. Settings for coalescing outgoing SignalR messages into a single envelope per flush.
/// </summary>
public class OutgoingCoalescingOptions
{
    private TimeSpan _flushInterval = 100.Milliseconds();
    private int _maxBatchSize = 200;

    /// <summary>
    ///     How long the transport accumulates outgoing messages for one destination before flushing them as a
    ///     single envelope. Default is 100ms.
    /// </summary>
    public TimeSpan FlushInterval
    {
        get => _flushInterval;
        set
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(FlushInterval),
                    "The flush interval must be greater than zero. Omit CoalesceOutgoing() entirely to send each message immediately.");
            }

            _flushInterval = value;
        }
    }

    /// <summary>
    ///     Flush as soon as this many messages have accumulated for one destination, without waiting out the
    ///     rest of <see cref="FlushInterval" />. Default is 200.
    /// </summary>
    public int MaxBatchSize
    {
        get => _maxBatchSize;
        set
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxBatchSize),
                    "The maximum batch size must be at least one message.");
            }

            _maxBatchSize = value;
        }
    }
}
