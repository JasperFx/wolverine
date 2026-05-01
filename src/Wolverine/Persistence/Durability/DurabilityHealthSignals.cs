using JasperFx;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Wolverine.Logging;

namespace Wolverine.Persistence.Durability;

/// <summary>
/// Shared, mutable state + evaluator that lets a durability-style agent surface richer
/// per-store health signals than the default `Status == Running ? Healthy : Unhealthy`.
/// One instance per agent. The agent's poll loop calls <see cref="RecordPollSuccess"/>
/// and <see cref="RecordPollFailure"/>; the agent's <c>CheckHealthAsync</c> implementation
/// calls <see cref="EvaluateAsync"/>, which folds in fresh persisted-counts deltas.
///
/// Three signals are layered on top of <see cref="AgentStatus"/>:
///
/// 1. <b>Persistence reachability</b> — driven by <see cref="RecordPollFailure"/> /
///    <see cref="RecordPollSuccess"/>. One failed cycle ⇒ Degraded; <see cref="DurabilitySettings.HealthConsecutiveFailureUnhealthyThreshold"/>
///    or more consecutive failures ⇒ Unhealthy. The most recent failure's message is
///    surfaced as the description so operators see e.g. <c>"Persistence unreachable: connection timeout"</c>.
///
/// 2. <b>Dead-letter growth</b> — at each <see cref="EvaluateAsync"/>, the previous
///    <see cref="PersistedCounts.DeadLetter"/> snapshot is compared against the current
///    one. If the rate exceeds <see cref="DurabilitySettings.HealthDeadLetterGrowthPerMinuteThreshold"/>,
///    Degraded.
///
/// 3. <b>Stuck recovery / scheduled-job pollers</b> — if persisted inbox+outbox or
///    scheduled counts stay non-zero and never decrease across
///    <see cref="DurabilitySettings.HealthStuckPollCycleThreshold"/> evaluations, Degraded.
///
/// Status precedence: Status != Running always returns Unhealthy first. Then
/// consecutive-failure Unhealthy. Otherwise the worst of the remaining signals
/// (Unhealthy &gt; Degraded &gt; Healthy) is returned.
/// </summary>
public sealed class DurabilityHealthSignals
{
    private readonly object _lock = new();
    private readonly DurabilitySettings _settings;

    private int _consecutiveFailures;
    private string? _lastFailureMessage;

    private PersistedCounts? _previousCounts;
    private DateTimeOffset _previousCountsAt;

    private int _stuckRecoveryCycles;
    private int _stuckScheduledCycles;

    public DurabilityHealthSignals(DurabilitySettings settings)
    {
        _settings = settings;
    }

    /// <summary>Reset the consecutive-failure counter after a clean poll cycle.</summary>
    public void RecordPollSuccess()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
            _lastFailureMessage = null;
        }
    }

    /// <summary>Bump the consecutive-failure counter and remember the most recent error message.</summary>
    public void RecordPollFailure(Exception exception)
    {
        if (exception is null) throw new ArgumentNullException(nameof(exception));

        lock (_lock)
        {
            _consecutiveFailures++;
            _lastFailureMessage = exception.Message;
        }
    }

    /// <summary>
    /// Test-only — surface the current consecutive-failure count.
    /// </summary>
    internal int ConsecutiveFailureCount
    {
        get { lock (_lock) return _consecutiveFailures; }
    }

    /// <summary>
    /// Compute a HealthCheckResult that folds the agent's <paramref name="status"/> together
    /// with the recorded poll-failure history and a fresh fetch of persisted counts. Pass
    /// <c>null</c> for <paramref name="counts"/> to skip the count-based signals (e.g. when
    /// the caller could not fetch counts because the store is down — the consecutive-failure
    /// signal will already capture that).
    /// </summary>
    public HealthCheckResult Evaluate(AgentStatus status, Uri agentUri, PersistedCounts? counts, DateTimeOffset now)
    {
        if (status != AgentStatus.Running)
        {
            return HealthCheckResult.Unhealthy($"Agent {agentUri} is {status}");
        }

        int consecutiveFailures;
        string? lastFailureMessage;
        lock (_lock)
        {
            consecutiveFailures = _consecutiveFailures;
            lastFailureMessage = _lastFailureMessage;
        }

        // Reachability — fail fast on consecutive failures before anything else.
        if (consecutiveFailures >= _settings.HealthConsecutiveFailureUnhealthyThreshold)
        {
            return HealthCheckResult.Unhealthy(
                $"Persistence unreachable for {consecutiveFailures} consecutive cycles" +
                (lastFailureMessage is null ? "" : $": {lastFailureMessage}"));
        }

        var degraded = new List<string>(capacity: 4);

        if (consecutiveFailures > 0)
        {
            degraded.Add($"Last persistence poll failed" +
                         (lastFailureMessage is null ? "" : $": {lastFailureMessage}"));
        }

        if (counts is not null)
        {
            // First evaluation: just snapshot and skip every count-based signal. Without a
            // previous baseline there's nothing meaningful to compare against; the next
            // evaluation will be the real one.
            if (_previousCounts is null)
            {
                _previousCounts = Snapshot(counts);
                _previousCountsAt = now;
            }
            else
            {
                EvaluateDeadLetterGrowth(counts, now, degraded);
                EvaluateStuckPollers(counts, degraded);
                _previousCounts = Snapshot(counts);
                _previousCountsAt = now;
            }
        }

        return degraded.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded(string.Join("; ", degraded));
    }

    private void EvaluateDeadLetterGrowth(PersistedCounts counts, DateTimeOffset now, List<string> degraded)
    {
        var deltaCount = counts.DeadLetter - _previousCounts!.DeadLetter;
        var elapsed = now - _previousCountsAt;
        if (elapsed > TimeSpan.Zero && deltaCount > 0)
        {
            var perMinute = deltaCount / Math.Max(elapsed.TotalMinutes, 1.0 / 60);
            if (perMinute >= _settings.HealthDeadLetterGrowthPerMinuteThreshold)
            {
                degraded.Add(
                    $"Dead-letter queue grew by {deltaCount} ({perMinute:F0}/min, threshold " +
                    $"{_settings.HealthDeadLetterGrowthPerMinuteThreshold}/min)");
            }
        }
    }

    private void EvaluateStuckPollers(PersistedCounts counts, List<string> degraded)
    {
        var threshold = _settings.HealthStuckPollCycleThreshold;

        var pendingNow = counts.Incoming + counts.Outgoing;
        var pendingPrev = _previousCounts!.Incoming + _previousCounts.Outgoing;
        if (pendingNow > 0 && pendingNow >= pendingPrev)
        {
            _stuckRecoveryCycles++;
            if (_stuckRecoveryCycles >= threshold)
            {
                degraded.Add(
                    $"Recovery batch may be stuck — {pendingNow} pending envelopes (inbox+outbox) " +
                    $"have not drained over {_stuckRecoveryCycles} consecutive checks");
            }
        }
        else
        {
            _stuckRecoveryCycles = 0;
        }

        if (counts.Scheduled > 0 && counts.Scheduled >= _previousCounts.Scheduled)
        {
            _stuckScheduledCycles++;
            if (_stuckScheduledCycles >= threshold)
            {
                degraded.Add(
                    $"Scheduled-job poller may be stuck — {counts.Scheduled} scheduled envelopes " +
                    $"have not drained over {_stuckScheduledCycles} consecutive checks");
            }
        }
        else
        {
            _stuckScheduledCycles = 0;
        }
    }

    private static PersistedCounts Snapshot(PersistedCounts source)
    {
        return new PersistedCounts
        {
            Incoming = source.Incoming,
            Outgoing = source.Outgoing,
            Scheduled = source.Scheduled,
            DeadLetter = source.DeadLetter,
            Handled = source.Handled
        };
    }
}
