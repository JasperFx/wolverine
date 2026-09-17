using JasperFx;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;
using Wolverine;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Xunit;

namespace CoreTests.Persistence;

// Unit tests for the per-agent health-signal aggregator added in #2646.
// The class is shared by the RDBMS, RavenDb, and CosmosDb durability agents - each
// owns one instance and feeds it RecordPollSuccess/RecordPollFailure from its
// background loop. CheckHealthAsync calls Evaluate(...) with a fresh PersistedCounts
// snapshot to fold in dead-letter-growth + stuck-poller signals on top of the
// reachability signal.
public class durability_health_signals_tests
{
    private static readonly Uri AgentUri = new("wolverinedb://test/durability");

    private static DurabilitySettings Settings(int unhealthyAfter = 3, int stuckAfter = 3, int dlqGrowthThreshold = 100)
    {
        return new DurabilitySettings
        {
            HealthConsecutiveFailureUnhealthyThreshold = unhealthyAfter,
            HealthStuckPollCycleThreshold = stuckAfter,
            HealthDeadLetterGrowthPerMinuteThreshold = dlqGrowthThreshold
        };
    }

    [Fact]
    public void unhealthy_when_status_is_not_running()
    {
        var signals = new DurabilityHealthSignals(Settings());

        var result = signals.Evaluate(AgentStatus.Stopped, AgentUri, counts: null, DateTimeOffset.UtcNow);

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description!.ShouldContain("Stopped");
    }

    [Fact]
    public void healthy_when_running_and_no_failures_and_counts_unchanged()
    {
        var signals = new DurabilityHealthSignals(Settings());
        var t0 = DateTimeOffset.UtcNow;
        var counts = new PersistedCounts();

        // First evaluation establishes baseline; second is the real check
        signals.Evaluate(AgentStatus.Running, AgentUri, counts, t0).Status.ShouldBe(HealthStatus.Healthy);
        signals.Evaluate(AgentStatus.Running, AgentUri, counts, t0.AddMinutes(1)).Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public void degraded_after_a_single_failure_then_healthy_after_success()
    {
        var signals = new DurabilityHealthSignals(Settings(unhealthyAfter: 3));
        signals.RecordPollFailure(new InvalidOperationException("simulated DB timeout"));

        var degraded = signals.Evaluate(AgentStatus.Running, AgentUri, counts: null, DateTimeOffset.UtcNow);
        degraded.Status.ShouldBe(HealthStatus.Degraded);
        degraded.Description!.ShouldContain("simulated DB timeout");

        signals.RecordPollSuccess();

        var healthy = signals.Evaluate(AgentStatus.Running, AgentUri, counts: null, DateTimeOffset.UtcNow);
        healthy.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public void unhealthy_after_consecutive_failure_threshold()
    {
        var signals = new DurabilityHealthSignals(Settings(unhealthyAfter: 3));

        signals.RecordPollFailure(new Exception("boom"));
        signals.RecordPollFailure(new Exception("boom"));
        signals.RecordPollFailure(new Exception("third strike"));

        var result = signals.Evaluate(AgentStatus.Running, AgentUri, counts: null, DateTimeOffset.UtcNow);

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description!.ShouldContain("3 consecutive cycles");
        result.Description!.ShouldContain("third strike");
    }

    [Fact]
    public void degraded_when_dead_letter_queue_grows_above_threshold()
    {
        var signals = new DurabilityHealthSignals(Settings(dlqGrowthThreshold: 100));
        var t0 = DateTimeOffset.UtcNow;

        // Baseline: 0 dead letters
        signals.Evaluate(AgentStatus.Running, AgentUri, new PersistedCounts(), t0);

        // 1 minute later: +200 dead letters -> 200/min, above the 100/min threshold
        var grown = new PersistedCounts { DeadLetter = 200 };
        var result = signals.Evaluate(AgentStatus.Running, AgentUri, grown, t0.AddMinutes(1));

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description!.ShouldContain("Dead-letter queue grew by 200");
    }

    [Fact]
    public void healthy_when_dead_letter_queue_growth_below_threshold()
    {
        var signals = new DurabilityHealthSignals(Settings(dlqGrowthThreshold: 100));
        var t0 = DateTimeOffset.UtcNow;

        signals.Evaluate(AgentStatus.Running, AgentUri, new PersistedCounts(), t0);

        var grown = new PersistedCounts { DeadLetter = 5 };
        var result = signals.Evaluate(AgentStatus.Running, AgentUri, grown, t0.AddMinutes(1));

        result.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public void degraded_when_recovery_pending_does_not_drain_for_threshold_cycles()
    {
        var signals = new DurabilityHealthSignals(Settings(stuckAfter: 3));
        var t0 = DateTimeOffset.UtcNow;
        var stuck = new PersistedCounts { Incoming = 50, Outgoing = 25 };

        // Baseline establishes pendingPrev = 75
        signals.Evaluate(AgentStatus.Running, AgentUri, stuck, t0);

        // Three more evals with same-or-higher pending
        signals.Evaluate(AgentStatus.Running, AgentUri, stuck, t0.AddSeconds(10)).Status.ShouldBe(HealthStatus.Healthy);
        signals.Evaluate(AgentStatus.Running, AgentUri, stuck, t0.AddSeconds(20)).Status.ShouldBe(HealthStatus.Healthy);

        var result = signals.Evaluate(AgentStatus.Running, AgentUri, stuck, t0.AddSeconds(30));
        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description!.ShouldContain("Recovery batch may be stuck");
        result.Description!.ShouldContain("75 pending");
    }

    [Fact]
    public void recovery_stuck_counter_resets_when_pending_decreases()
    {
        var signals = new DurabilityHealthSignals(Settings(stuckAfter: 3));
        var t0 = DateTimeOffset.UtcNow;

        var heavy = new PersistedCounts { Incoming = 100 };
        signals.Evaluate(AgentStatus.Running, AgentUri, heavy, t0);
        signals.Evaluate(AgentStatus.Running, AgentUri, heavy, t0.AddSeconds(10));
        signals.Evaluate(AgentStatus.Running, AgentUri, heavy, t0.AddSeconds(20));

        // Pending drains; counter should reset
        var lighter = new PersistedCounts { Incoming = 10 };
        var result = signals.Evaluate(AgentStatus.Running, AgentUri, lighter, t0.AddSeconds(30));
        result.Status.ShouldBe(HealthStatus.Healthy);

        // Have to climb back up the threshold from zero
        signals.Evaluate(AgentStatus.Running, AgentUri, lighter, t0.AddSeconds(40)).Status.ShouldBe(HealthStatus.Healthy);
        signals.Evaluate(AgentStatus.Running, AgentUri, lighter, t0.AddSeconds(50)).Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public void degraded_when_DUE_scheduled_count_does_not_drain()
    {
        var signals = new DurabilityHealthSignals(Settings(stuckAfter: 3));
        var t0 = DateTimeOffset.UtcNow;
        var pending = new PersistedCounts { Scheduled = 42, ScheduledDue = 42 };

        signals.Evaluate(AgentStatus.Running, AgentUri, pending, t0);
        signals.Evaluate(AgentStatus.Running, AgentUri, pending, t0.AddSeconds(10));
        signals.Evaluate(AgentStatus.Running, AgentUri, pending, t0.AddSeconds(20));
        var result = signals.Evaluate(AgentStatus.Running, AgentUri, pending, t0.AddSeconds(30));

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description!.ShouldContain("Scheduled-job poller may be stuck");
        result.Description!.ShouldContain("42 scheduled");
        result.Description!.ShouldContain("past their execution time");
    }

    [Fact]
    public void healthy_when_scheduled_envelopes_are_simply_not_due_yet()
    {
        // The field case: an application schedules work with a deliberate quiet period (15 minutes on
        // the deployment that produced this), so the scheduled count sits constant and never drains.
        // That is the scheduled queue working. Reading the undifferentiated count made 254 of 271
        // active alerts this one check, over 265 databases — 114 of them "degraded" over ONE envelope.
        var signals = new DurabilityHealthSignals(Settings(stuckAfter: 3));
        var t0 = DateTimeOffset.UtcNow;
        var waiting = new PersistedCounts { Scheduled = 19, ScheduledDue = 0 };

        for (var i = 0; i < 6; i++)
        {
            signals.Evaluate(AgentStatus.Running, AgentUri, waiting, t0.AddSeconds(10 * i))
                .Status.ShouldBe(HealthStatus.Healthy);
        }
    }

    [Fact]
    public void stands_down_when_the_store_does_not_report_due_counts()
    {
        // ⚠️ null is NOT MEASURED, not zero. A store that cannot answer must make this signal silent
        // rather than fall back to the undifferentiated count — falling back is the defect.
        var signals = new DurabilityHealthSignals(Settings(stuckAfter: 3));
        var t0 = DateTimeOffset.UtcNow;
        var unreported = new PersistedCounts { Scheduled = 5_000, ScheduledDue = null };

        for (var i = 0; i < 6; i++)
        {
            signals.Evaluate(AgentStatus.Running, AgentUri, unreported, t0.AddSeconds(10 * i))
                .Status.ShouldBe(HealthStatus.Healthy);
        }
    }

    [Fact]
    public void a_draining_due_backlog_resets_the_counter()
    {
        var signals = new DurabilityHealthSignals(Settings(stuckAfter: 3));
        var t0 = DateTimeOffset.UtcNow;

        var stuck = new PersistedCounts { Scheduled = 40, ScheduledDue = 40 };
        signals.Evaluate(AgentStatus.Running, AgentUri, stuck, t0);
        signals.Evaluate(AgentStatus.Running, AgentUri, stuck, t0.AddSeconds(10));
        signals.Evaluate(AgentStatus.Running, AgentUri, stuck, t0.AddSeconds(20));

        // The poller moves them: due count falls, so the run of consecutive checks restarts.
        var draining = new PersistedCounts { Scheduled = 40, ScheduledDue = 3 };
        signals.Evaluate(AgentStatus.Running, AgentUri, draining, t0.AddSeconds(30))
            .Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public void status_takes_precedence_over_count_signals()
    {
        // Even with a clean recent history, a non-Running status flips Unhealthy.
        var signals = new DurabilityHealthSignals(Settings());
        signals.RecordPollSuccess();

        var result = signals.Evaluate(AgentStatus.Stopped, AgentUri, new PersistedCounts(), DateTimeOffset.UtcNow);

        result.Status.ShouldBe(HealthStatus.Unhealthy);
    }

    [Fact]
    public void multiple_degraded_signals_aggregate_into_one_description()
    {
        var signals = new DurabilityHealthSignals(Settings(stuckAfter: 2, dlqGrowthThreshold: 10));
        var t0 = DateTimeOffset.UtcNow;

        signals.Evaluate(AgentStatus.Running, AgentUri, new PersistedCounts { Incoming = 50 }, t0);
        signals.Evaluate(AgentStatus.Running, AgentUri,
            new PersistedCounts { Incoming = 50, DeadLetter = 100 }, t0.AddMinutes(1));

        // One more tick at the threshold; DLQ stays put (no further growth) so only the stuck signal fires now.
        signals.RecordPollFailure(new Exception("transient blip"));
        var result = signals.Evaluate(AgentStatus.Running, AgentUri,
            new PersistedCounts { Incoming = 50, DeadLetter = 100 }, t0.AddMinutes(2));

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description!.ShouldContain("Last persistence poll failed");
        result.Description!.ShouldContain("Recovery batch may be stuck");
    }

    [Fact]
    public void exposes_consecutive_failure_count_for_diagnostics()
    {
        var signals = new DurabilityHealthSignals(Settings());
        signals.ConsecutiveFailureCount.ShouldBe(0);

        signals.RecordPollFailure(new Exception("a"));
        signals.RecordPollFailure(new Exception("b"));
        signals.ConsecutiveFailureCount.ShouldBe(2);

        signals.RecordPollSuccess();
        signals.ConsecutiveFailureCount.ShouldBe(0);
    }
}
