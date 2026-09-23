using JasperFx;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;
using Wolverine;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Xunit;

namespace CoreTests.Persistence;

/// <summary>
/// GH-4499. <see cref="DurabilityHealthSignals" />'s recovery-batch check judged "stuck" from inbox+outbox
/// DEPTH alone, and a service under steady load holds a roughly constant non-zero depth as envelopes drain
/// and are replaced between polls. So a busy store read as Degraded while doing exactly what it should.
/// </summary>
/// <remarks>
/// <para>GH-4476's principle, a second time: a count does not say whether the rows are the SAME rows. That
/// was solved for the scheduled poller by adding <c>ScheduledDue</c> as a discriminator; the recovery check
/// had no equivalent and was inferring movement from a quantity that movement does not change.</para>
///
/// <para>The discriminator here is <see cref="PersistedCounts.Handled" /> rising -- work completed since
/// the last poll -- which the store already reports, so it costs no new query.</para>
///
/// <para>On the fleet that produced GH-4476 the scheduled check was 254 of 271 active alerts and this
/// sibling was another 12, and <c>AgentHealth</c> feeds the fleet health roll-ups.</para>
/// </remarks>
public class steady_state_is_not_a_stuck_recovery_batch_4499
{
    private static readonly Uri AgentUri = new("wolverinedb://test/durability");

    private static DurabilitySettings Settings(int stuckAfter = 3)
    {
        return new DurabilitySettings
        {
            HealthConsecutiveFailureUnhealthyThreshold = 3,
            HealthStuckPollCycleThreshold = stuckAfter,
            HealthDeadLetterGrowthPerMinuteThreshold = 100
        };
    }

    /// <summary>
    /// The report's repro. Flat depth, a thousand more envelopes handled every poll.
    /// </summary>
    [Fact]
    public void steady_state_throughput_is_not_reported_as_stuck()
    {
        var signals = new DurabilityHealthSignals(Settings());
        var t = DateTimeOffset.UtcNow;

        HealthCheckResult result = default;
        for (var i = 0; i < 5; i++)
        {
            var counts = new PersistedCounts
            {
                Incoming = 50,              // flat depth -- envelopes drain and are replaced
                Outgoing = 0,
                Handled = 1000 * (i + 1)    // ...while a thousand more drain every single poll
            };

            result = signals.Evaluate(AgentStatus.Running, AgentUri, counts, t.AddSeconds(30 * i));
        }

        result.Status.ShouldBe(HealthStatus.Healthy);
    }

    /// <summary>
    /// The true positive this check exists for, and the other half of the report. Identical flat depth --
    /// only the absence of completed work separates it from the case above.
    /// </summary>
    [Fact]
    public void a_genuinely_stuck_recovery_batch_is_still_reported()
    {
        var signals = new DurabilityHealthSignals(Settings());
        var t = DateTimeOffset.UtcNow;

        HealthCheckResult result = default;
        for (var i = 0; i < 5; i++)
        {
            // Handled FLAT: nothing is completing
            var counts = new PersistedCounts { Incoming = 50, Outgoing = 0, Handled = 1000 };
            result = signals.Evaluate(AgentStatus.Running, AgentUri, counts, t.AddSeconds(30 * i));
        }

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description!.ShouldContain("Recovery batch may be stuck");
    }

    /// <summary>
    /// A rising depth with nothing completing is the worst case, and must still fire.
    /// </summary>
    [Fact]
    public void a_growing_backlog_with_no_completions_is_still_reported()
    {
        var signals = new DurabilityHealthSignals(Settings());
        var t = DateTimeOffset.UtcNow;

        HealthCheckResult result = default;
        for (var i = 0; i < 5; i++)
        {
            var counts = new PersistedCounts { Incoming = 50 + 10 * i, Handled = 0 };
            result = signals.Evaluate(AgentStatus.Running, AgentUri, counts, t.AddSeconds(30 * i));
        }

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description!.ShouldContain("Recovery batch may be stuck");
    }

    /// <summary>
    /// One poll's worth of progress resets the counter -- the batch is moving, whatever it did before.
    /// </summary>
    [Fact]
    public void a_single_poll_of_progress_clears_the_counter()
    {
        var signals = new DurabilityHealthSignals(Settings());
        var t = DateTimeOffset.UtcNow;

        // Two cycles of no progress, one short of the threshold
        signals.Evaluate(AgentStatus.Running, AgentUri, new PersistedCounts { Incoming = 50 }, t);
        signals.Evaluate(AgentStatus.Running, AgentUri, new PersistedCounts { Incoming = 50 }, t.AddSeconds(30));
        signals.Evaluate(AgentStatus.Running, AgentUri, new PersistedCounts { Incoming = 50 }, t.AddSeconds(60))
            .Status.ShouldBe(HealthStatus.Healthy);

        // Something completed
        signals.Evaluate(AgentStatus.Running, AgentUri, new PersistedCounts { Incoming = 50, Handled = 1 },
            t.AddSeconds(90)).Status.ShouldBe(HealthStatus.Healthy);

        // ...so the next flat poll starts counting from zero rather than tripping the threshold
        signals.Evaluate(AgentStatus.Running, AgentUri, new PersistedCounts { Incoming = 50, Handled = 1 },
            t.AddSeconds(120)).Status.ShouldBe(HealthStatus.Healthy);
    }

    /// <summary>
    /// The handled-row sweep drops <see cref="PersistedCounts.Handled" />. That is the cleanup timer doing
    /// its job, and says nothing about the recovery batch -- so it must not be read as "no progress", or the
    /// sweep itself would raise an alert on a busy store.
    /// </summary>
    [Fact]
    public void the_handled_row_sweep_does_not_count_as_no_progress()
    {
        var signals = new DurabilityHealthSignals(Settings());
        var t = DateTimeOffset.UtcNow;

        // Handled falls every poll, as the cleanup timer reaps faster than work completes
        HealthCheckResult result = default;
        for (var i = 0; i < 5; i++)
        {
            var counts = new PersistedCounts { Incoming = 50, Handled = 5000 - 1000 * i };
            result = signals.Evaluate(AgentStatus.Running, AgentUri, counts, t.AddSeconds(30 * i));
        }

        result.Status.ShouldBe(HealthStatus.Healthy);
    }

    /// <summary>
    /// Pins the one case this fix deliberately gives up, so it is a known limitation rather than a surprise.
    /// <see cref="PersistedCounts.Handled" /> tracks the INBOX only -- a successfully sent outgoing envelope
    /// is deleted rather than marked, and PersistedCounts has no outbox equivalent. A stuck outbox alongside
    /// a busy inbox therefore reads as healthy.
    /// </summary>
    /// <remarks>
    /// Standing down on evidence the check cannot interpret is the same rule <c>ScheduledDue</c>'s null case
    /// sets, and it is the direction that does not manufacture alerts. Catching this needs a "sent since the
    /// last poll" counter from the store.
    /// </remarks>
    [Fact]
    public void a_stuck_outbox_behind_a_busy_inbox_is_a_known_blind_spot()
    {
        var signals = new DurabilityHealthSignals(Settings());
        var t = DateTimeOffset.UtcNow;

        HealthCheckResult result = default;
        for (var i = 0; i < 5; i++)
        {
            var counts = new PersistedCounts
            {
                Incoming = 10,
                Outgoing = 500,             // wedged, and never moves
                Handled = 1000 * (i + 1)    // while the inbox hums along
            };

            result = signals.Evaluate(AgentStatus.Running, AgentUri, counts, t.AddSeconds(30 * i));
        }

        result.Status.ShouldBe(HealthStatus.Healthy);
    }
}
