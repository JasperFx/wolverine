using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Wolverine.Transports;

namespace Wolverine.Runtime.WorkerQueues;

/// <summary>
///     GH-4048. Keeps the broker's clock alive on every delivery that <see cref="NativeAckReceiver" /> has enqueued
///     but not yet settled, and decides what happens when that clock is lost anyway.
/// </summary>
/// <remarks>
///     <para>
///         This is a generalization of <c>SqsVisibilityHeartbeat</c>, which solved the same problem for a single
///         transport and a single mode. NativeAck makes the problem universal: the delivery is held unsettled for
///         lane queue time <em>plus</em> handler time, and lane queue time is unbounded by design.
///     </para>
///     <para>
///         The tick is a maximum age rather than a debounce, and nothing is sent on a tick when nothing is in
///         flight -- an endpoint whose lanes stay shallow never issues a single renewal call. Renewals are grouped
///         by <see cref="Envelope.Listener" /> because a receiver can serve several listeners
///         (<c>ListenerCount &gt; 1</c>, per-tenant compound listeners) and, exactly as with settlement, a renewal
///         has to go to the listener the delivery actually arrived on.
///     </para>
/// </remarks>
internal class LeaseRenewalTracker : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, Tracked> _inFlight = new();

    // Envelopes whose lease has been lost, still sitting in an execution lane. An entry is consumed by
    // NativeAckReceiver -- dropped outright if it had not started, or (if it had) left in place so the receiver
    // knows to suppress its defer -- and removed by Untrack. Bounded by the same broker prefetch window as
    // _inFlight, and swept by age so an entry whose envelope never reaches the block cannot linger.
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lost = new();

    private readonly TimeSpan _leaseDuration;
    private readonly TimeSpan _maximumExtension;
    private readonly bool _requiresExplicitRenewal;
    private readonly TimeSpan _interval;
    private readonly ILogger _logger;
    private readonly Uri _uri;
    private readonly CancellationTokenSource _cancellation;
    private readonly Task _loop;

    private readonly Counter<int>? _renewedCounter;
    private readonly Counter<int>? _lostCounter;
    private readonly Counter<int>? _ceilingCounter;
    private readonly KeyValuePair<string, object?> _endpointTag;

    /// <param name="template">
    ///     The first lease-renewing listener seen on this endpoint. Every listener built for one endpoint reads the
    ///     same configuration, so the durations are taken once from here; the renewal call itself always goes to
    ///     the listener that delivered the envelope.
    /// </param>
    /// <param name="uri">Endpoint Uri, for logging and metric tags</param>
    /// <param name="logger"></param>
    /// <param name="meter">Runtime meter. Null in tests that only care about the scheduling</param>
    /// <param name="cancellation">Runtime shutdown</param>
    /// <param name="interval">Tick interval. Defaults to half the lease duration, never under one second</param>
    public LeaseRenewalTracker(ISupportLeaseRenewal template, Uri uri, ILogger logger, Meter? meter,
        CancellationToken cancellation, TimeSpan? interval = null)
    {
        _leaseDuration = template.LeaseDuration;
        _maximumExtension = template.MaximumLeaseExtension;
        _requiresExplicitRenewal = template.RequiresExplicitRenewal;
        _uri = uri;
        _logger = logger;

        var half = TimeSpan.FromTicks(_leaseDuration.Ticks / 2);
        _interval = interval ?? (half < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : half);

        _endpointTag = new KeyValuePair<string, object?>(MetricsConstants.MessageDestinationKey, uri.ToString());

        if (meter != null)
        {
            _renewedCounter = meter.CreateCounter<int>(MetricsConstants.LeasesRenewed, MetricsConstants.Messages,
                "Number of broker leases renewed while an envelope waited in a native-ack execution lane");
            _lostCounter = meter.CreateCounter<int>(MetricsConstants.LeasesLost, MetricsConstants.Messages,
                "Number of broker leases lost on an unsettled envelope, tagged by whether it had started executing");
            _ceilingCounter = meter.CreateCounter<int>(MetricsConstants.LeaseCeilingReached, MetricsConstants.Messages,
                "Number of envelopes that hit the maximum lease extension and stopped being renewed");
        }

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _loop = Task.Run(runAsync);
    }

    public TimeSpan Interval => _interval;

    /// <summary>Number of envelopes whose lease is currently being kept alive</summary>
    public int InFlightCount => _inFlight.Count;

    /// <summary>Number of lease-lost envelopes still sitting in a lane waiting to be dropped</summary>
    public int LostCount => _lost.Count;

    // Local counters alongside the OTel instruments so tests -- and a debugger -- can read the same numbers
    // without standing up a MeterListener.
    public int LeasesRenewed { get; private set; }
    public int LeasesLostBeforeStarting { get; private set; }
    public int LeasesLostWhileExecuting { get; private set; }
    public int CeilingsReached { get; private set; }

    /// <summary>
    ///     Start keeping this envelope's delivery alive. No-op unless it arrived on a lease-renewing listener.
    /// </summary>
    public void Track(Envelope envelope)
    {
        if (envelope.Listener is not ISupportLeaseRenewal renewal) return;

        var receivedAt = envelope.ReceivedAt ?? DateTimeOffset.UtcNow;
        _inFlight.TryAdd(envelope.Id, new Tracked(envelope, renewal, receivedAt));
    }

    /// <summary>
    ///     The gate in front of <c>Pipeline.InvokeAsync</c>. Returns false when this envelope's lease is already
    ///     gone and it must be dropped -- neither completed nor deferred. Returns true otherwise, and atomically
    ///     marks the envelope as executing so a lease lost from here on is classified as realized duplication
    ///     rather than prevented duplication.
    /// </summary>
    public bool TryBeginExecution(Envelope envelope)
    {
        if (_lost.ContainsKey(envelope.Id)) return false;

        if (_inFlight.TryGetValue(envelope.Id, out var tracked))
        {
            lock (tracked.Gate)
            {
                // The tick can mark this lost between the _lost probe above and this lock; the same lock is
                // what makes "lost or started" a single decision rather than a race.
                if (tracked.IsLost) return false;
                tracked.HasStarted = true;
            }
        }

        return true;
    }

    /// <summary>
    ///     Has this envelope's lease been lost? True for the whole time it is still in the lane, so a caller
    ///     that has already started executing can suppress its defer -- deferring after the lease is gone
    ///     republishes a second copy on top of the broker's redelivery.
    /// </summary>
    public bool WasLeaseLost(Envelope envelope)
    {
        return _lost.ContainsKey(envelope.Id);
    }

    /// <summary>
    ///     This envelope has reached a terminal (or is being abandoned); stop renewing it.
    /// </summary>
    public void Untrack(Envelope envelope)
    {
        _inFlight.TryRemove(envelope.Id, out _);
        _lost.TryRemove(envelope.Id, out _);
    }

    private async Task runAsync()
    {
        var token = _cancellation.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_inFlight.IsEmpty && _lost.IsEmpty) continue;

            try
            {
                await TickAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Error renewing the broker leases of in-flight envelopes at {Uri}", _uri);
            }
        }
    }

    /// <summary>
    ///     One pass: retire anything past the ceiling, renew the rest, mark refusals lost, then infer loss for
    ///     anything that has gone a full lease duration without a successful renewal. Exposed for tests.
    /// </summary>
    internal async Task TickAsync(CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;

        sweepLostSet(now);

        var due = new List<Tracked>();
        foreach (var pair in _inFlight)
        {
            var tracked = pair.Value;

            // Every renewal pushes the delivery out by a full lease, so stop before one would carry it past
            // the maximum. Deliberately not a lost lease: the delivery may still finish inside what it holds.
            if (now - tracked.ReceivedAt + _leaseDuration > _maximumExtension)
            {
                if (_inFlight.TryRemove(pair.Key, out _))
                {
                    CeilingsReached++;
                    _ceilingCounter?.Add(1, _endpointTag);
                    _logger.LogWarning(
                        "Envelope {EnvelopeId} at {Uri} has been unsettled for {Elapsed} and will not have its broker lease renewed past the maximum of {Maximum}; the broker may redeliver it while it is still queued or being handled",
                        tracked.Envelope.Id, _uri, now - tracked.ReceivedAt, _maximumExtension);
                }

                continue;
            }

            due.Add(tracked);
        }

        if (_requiresExplicitRenewal && due.Count > 0)
        {
            foreach (var group in due.GroupBy(x => x.Listener))
            {
                await renewGroupAsync(group.Key, group.ToArray(), token).ConfigureAwait(false);
            }
        }

        // The second detector, and the only one available on a transport whose renewal call cannot report a
        // per-message failure. Skipped when the transport renews for us -- there are no renewals to age.
        if (_requiresExplicitRenewal)
        {
            inferLoss(DateTimeOffset.UtcNow);
        }
    }

    private async Task renewGroupAsync(ISupportLeaseRenewal listener, Tracked[] group, CancellationToken token)
    {
        var envelopes = group.Select(x => x.Envelope).ToArray();

        IReadOnlyList<Envelope> refused;
        try
        {
            refused = await listener.RenewLeasesAsync(envelopes, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            // Transient by contract. Keep tracking and try again on the next tick -- a renewal that never
            // lands is caught by the inferred detector below rather than by guessing here.
            _logger.LogWarning(e, "Error renewing the broker leases of {Count} envelopes at {Uri}", group.Length, _uri);
            return;
        }

        var refusedIds = refused.Count == 0 ? null : refused.Select(x => x.Id).ToHashSet();
        var renewedAt = DateTimeOffset.UtcNow;

        foreach (var tracked in group)
        {
            if (refusedIds != null && refusedIds.Contains(tracked.Envelope.Id))
            {
                markLost(tracked, renewedAt, "the broker refused to renew it");
                continue;
            }

            lock (tracked.Gate)
            {
                tracked.LastRenewedAt = renewedAt;
            }

            LeasesRenewed++;
            _renewedCounter?.Add(1, _endpointTag);
        }
    }

    private void inferLoss(DateTimeOffset now)
    {
        foreach (var pair in _inFlight)
        {
            var tracked = pair.Value;
            DateTimeOffset last;
            lock (tracked.Gate)
            {
                last = tracked.LastRenewedAt;
            }

            if (now - last > _leaseDuration)
            {
                markLost(tracked, now, $"no renewal has succeeded for {now - last}");
            }
        }
    }

    private void markLost(Tracked tracked, DateTimeOffset now, string reason)
    {
        bool hadStarted;
        lock (tracked.Gate)
        {
            if (tracked.IsLost) return;
            tracked.IsLost = true;
            hadStarted = tracked.HasStarted;
        }

        _inFlight.TryRemove(tracked.Envelope.Id, out _);
        _lost.TryAdd(tracked.Envelope.Id, now);

        if (hadStarted)
        {
            // Nothing to be done: a running handler cannot be un-run, and the broker is redelivering. Meter it
            // so an operator sees realized duplication instead of inferring it.
            LeasesLostWhileExecuting++;
            _lostCounter?.Add(1, _endpointTag, ExecutingTag);
            _logger.LogWarning(
                "Lost the broker lease on envelope {EnvelopeId} at {Uri} after {Elapsed} while it was already executing ({Reason}). The broker owns this delivery again, so it may be handled more than once",
                tracked.Envelope.Id, _uri, now - tracked.ReceivedAt, reason);
        }
        else
        {
            // The fixable case: NativeAckReceiver.executeAsync will drop it rather than run a second copy.
            LeasesLostBeforeStarting++;
            _lostCounter?.Add(1, _endpointTag, NotStartedTag);
            _logger.LogWarning(
                "Lost the broker lease on envelope {EnvelopeId} at {Uri} after {Elapsed} before it started executing ({Reason}). It will be dropped from the execution lane without being completed or deferred so that the broker's redelivery is the only remaining copy",
                tracked.Envelope.Id, _uri, now - tracked.ReceivedAt, reason);
        }
    }

    private void sweepLostSet(DateTimeOffset now)
    {
        if (_lost.IsEmpty) return;

        foreach (var pair in _lost)
        {
            // Past the ceiling the envelope can no longer be in the lane for any reason this tracker cares
            // about -- either it was dropped or its lane was drained with the receiver.
            if (now - pair.Value > _maximumExtension)
            {
                _lost.TryRemove(pair.Key, out _);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            // The loop exits on the next tick at the latest; bound the wait so disposing a receiver can never
            // hang on it
            await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cancelled or timed out -- either way the loop is done with as far as we care
        }

        _inFlight.Clear();
        _lost.Clear();
        _cancellation.Dispose();
    }

    private static readonly KeyValuePair<string, object?> NotStartedTag =
        new(MetricsConstants.LeaseLossStageKey, "not-started");

    private static readonly KeyValuePair<string, object?> ExecutingTag =
        new(MetricsConstants.LeaseLossStageKey, "executing");

    private sealed class Tracked
    {
        public Tracked(Envelope envelope, ISupportLeaseRenewal listener, DateTimeOffset receivedAt)
        {
            Envelope = envelope;
            Listener = listener;
            ReceivedAt = receivedAt;
            LastRenewedAt = receivedAt;
        }

        public Envelope Envelope { get; }
        public ISupportLeaseRenewal Listener { get; }
        public DateTimeOffset ReceivedAt { get; }

        /// <summary>Guards the LastRenewedAt / HasStarted / IsLost transitions as one decision</summary>
        public object Gate { get; } = new();

        public DateTimeOffset LastRenewedAt { get; set; }
        public bool HasStarted { get; set; }
        public bool IsLost { get; set; }
    }
}
