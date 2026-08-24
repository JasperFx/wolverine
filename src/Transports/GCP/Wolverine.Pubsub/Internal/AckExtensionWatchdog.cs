using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Wolverine.Pubsub.Internal;

/// <summary>
/// GH-4066. Watches how long each Google Cloud Platform Pub/Sub delivery has been held inside its subscriber
/// callback and warns when one outlives <see cref="PubsubClientOptions.MaxTotalAckExtension" />.
/// <para>
/// This matters because of how the Pub/Sub client behaves at that boundary: it stops extending the ack deadline
/// and then does nothing else. The running callback is not cancelled, receives no exception, and is told nothing.
/// The service simply redelivers the message once the last extended deadline lapses, so a <em>second</em>
/// execution starts while the first is still running. Wolverine's at-least-once contract covers a message
/// arriving twice, but not a message overlapping itself — that breaks optimistic concurrency, event stream
/// appends, sagas, and the intra-group ordering guarantee. Before this watchdog the first sign of any of it was
/// downstream corruption.
/// </para>
/// <para>
/// One shared periodic scan per listener rather than a timer per message: tracking is two dictionary operations
/// on the receive hot path, which matters because <c>BufferedInMemory</c> and <c>Durable</c> endpoints run this
/// path at full broker throughput while never getting anywhere near the budget.
/// </para>
/// </summary>
internal sealed class AckExtensionWatchdog : IAsyncDisposable
{
    /// <summary>
    /// Clamps the scan interval. The warning fires on the first scan after the budget elapses, so the interval is
    /// also the worst-case reporting lag.
    /// </summary>
    internal static readonly TimeSpan MinimumScanInterval = TimeSpan.FromSeconds(1);

    internal static readonly TimeSpan MaximumScanInterval = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _budget;
    private readonly ConcurrentDictionary<long, Delivery> _inFlight = new();
    private readonly ILogger _logger;
    private readonly Timer _timer;
    private readonly Uri _uri;

    private long _ticket;

    public AckExtensionWatchdog(Uri uri, TimeSpan budget, ILogger logger)
    {
        _uri = uri;
        _budget = budget;
        _logger = logger;

        ScanInterval = determineScanInterval(budget);
        _timer = new Timer(_ => scan(), null, ScanInterval, ScanInterval);
    }

    internal TimeSpan ScanInterval { get; }

    /// <summary>
    /// Exposed for testing -- the number of deliveries currently being watched.
    /// </summary>
    internal int InFlightCount => _inFlight.Count;

    public async ValueTask DisposeAsync()
    {
        // Timer.DisposeAsync() completes once any in-flight scan callback has finished
        await _timer.DisposeAsync();
        _inFlight.Clear();
    }

    /// <summary>
    /// Begin watching a delivery. The returned ticket must be handed back to <see cref="Release" /> when the
    /// subscriber callback returns.
    /// </summary>
    public long Track(string? messageId)
    {
        var ticket = Interlocked.Increment(ref _ticket);
        _inFlight[ticket] = new Delivery(messageId, Stopwatch.GetTimestamp());

        return ticket;
    }

    public void Release(long ticket)
    {
        _inFlight.TryRemove(ticket, out _);
    }

    /// <summary>
    /// Warn about every tracked delivery that has now outlived the ack extension budget. Each delivery is only
    /// reported once. Internal so tests can drive it deterministically instead of waiting on the timer.
    /// </summary>
    internal void CheckForExpiredDeliveries()
    {
        var now = Stopwatch.GetTimestamp();

        foreach (var pair in _inFlight)
        {
            var delivery = pair.Value;
            if (delivery.Warned)
            {
                continue;
            }

            var elapsed = Stopwatch.GetElapsedTime(delivery.StartedAt, now);
            if (elapsed < _budget)
            {
                continue;
            }

            // Losing the race just means somebody else already warned about this delivery
            if (!delivery.TryMarkWarned())
            {
                continue;
            }

            _logger.LogWarning(
                "{Uri}: Google Cloud Platform Pub/Sub message {MessageId} has been processing for {Elapsed}, which exceeds the MaxTotalAckExtension budget of {Budget}. Wolverine's Pub/Sub client has stopped extending this message's ack deadline, so Pub/Sub will redeliver it and a SECOND, CONCURRENT execution of the same message will begin while this one is still running. Handlers that are not safe to run concurrently with themselves -- optimistic concurrency, event stream appends, sagas, group-ordered processing -- can be corrupted by this. Either shorten the handler or raise MaxTotalAckExtension via ConfigureListener(x => x.MaxTotalAckExtension = ...).",
                _uri,
                delivery.MessageId ?? "(unknown)",
                elapsed,
                _budget);
        }
    }

    /// <summary>
    /// Scan often enough that the warning lands close to the crossing, without spinning for an endpoint whose
    /// budget is measured in hours.
    /// </summary>
    private static TimeSpan determineScanInterval(TimeSpan budget)
    {
        var interval = TimeSpan.FromTicks(budget.Ticks / 10);

        if (interval < MinimumScanInterval)
        {
            return MinimumScanInterval;
        }

        return interval > MaximumScanInterval ? MaximumScanInterval : interval;
    }

    /// <summary>
    /// Scans may overlap if one runs long; <see cref="CheckForExpiredDeliveries" /> is idempotent, so that is
    /// harmless. Nothing in here may be allowed to throw onto the timer's thread pool thread.
    /// </summary>
    private void scan()
    {
        try
        {
            CheckForExpiredDeliveries();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "{Uri}: Error in the Pub/Sub ack extension watchdog.", _uri);
        }
    }

    private sealed class Delivery
    {
        private int _warned;

        public Delivery(string? messageId, long startedAt)
        {
            MessageId = messageId;
            StartedAt = startedAt;
        }

        public string? MessageId { get; }
        public long StartedAt { get; }
        public bool Warned => Volatile.Read(ref _warned) == 1;

        public bool TryMarkWarned()
        {
            return Interlocked.CompareExchange(ref _warned, 1, 0) == 0;
        }
    }
}
