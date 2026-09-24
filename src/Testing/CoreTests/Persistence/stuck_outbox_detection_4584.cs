using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;
using Wolverine;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Xunit;

namespace CoreTests.Persistence;

/// <summary>
/// Follow-up to GH-4499. That fix gated the recovery-batch check on <see cref="PersistedCounts.Handled" />
/// rising, which cured the false positives but gave up detection of a stuck OUTBOX: <c>Handled</c> counts
/// inbox completions, and a successfully sent outgoing envelope is deleted rather than marked, so there is
/// no completion counter to read for the outbox.
/// </summary>
/// <remarks>
/// <para>The head of the queue answers it instead, and answers it better than a counter would: a draining
/// outbox keeps replacing its oldest row, so <see cref="PersistedCounts.OldestOutgoing" /> advances; a stuck
/// one does not move. That is GH-4476's question — are these the SAME rows — answered directly rather than
/// inferred from a quantity.</para>
///
/// <para>null is NOT MEASURED. The outgoing table carries a timestamp column only when
/// <c>OutboxStaleTime</c> is set, so most stores say nothing here and the signal stands down.</para>
/// </remarks>
public class stuck_outbox_detection_4584
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

    private const string TheMessage = "Outbox may be stuck";

    /// <summary>
    /// Asserts only that the OUTBOX signal is silent, rather than that the whole result is Healthy.
    /// </summary>
    /// <remarks>
    /// Deliberate: the recovery-batch check in the same method judges inbox+outbox DEPTH, and several of
    /// these fixtures hold a flat non-zero depth on purpose, so it has its own opinion. GH-4499 (#4584) is
    /// what quiets that one, and this branch is cut from main rather than stacked on it. Scoping the
    /// assertion to this signal keeps these tests true either way, and Description is null on a Healthy
    /// result, so it needs coalescing regardless.
    /// </remarks>
    private static void ShouldNotReportStuckOutbox(HealthCheckResult result)
        => (result.Description ?? "").ShouldNotContain(TheMessage);

    /// <summary>
    /// The case GH-4499 gave up, now caught: the outbox never moves while the inbox hums along.
    /// </summary>
    [Fact]
    public void a_wedged_outbox_behind_a_busy_inbox_is_reported()
    {
        var signals = new DurabilityHealthSignals(Settings());
        var t = DateTimeOffset.UtcNow;
        var head = t.AddMinutes(-20);

        HealthCheckResult result = default;
        for (var i = 0; i < 5; i++)
        {
            var counts = new PersistedCounts
            {
                Incoming = 10,
                Outgoing = 500,
                OldestOutgoing = head,      // never moves
                Handled = 1000 * (i + 1)    // the inbox is fine
            };

            result = signals.Evaluate(AgentStatus.Running, AgentUri, counts, t.AddSeconds(30 * i));
        }

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description!.ShouldContain(TheMessage);

        // The message names the depth and how long the head has been sitting there, which is the part an
        // operator can act on
        result.Description!.ShouldContain("500 pending outgoing envelopes");
    }

    /// <summary>
    /// The false positive this has to avoid, and the reason a depth reading was not enough: a busy outbox
    /// holds a constant depth while its head advances every poll.
    /// </summary>
    [Fact]
    public void a_busy_outbox_at_a_constant_depth_is_not_reported()
    {
        var signals = new DurabilityHealthSignals(Settings());
        var t = DateTimeOffset.UtcNow;

        HealthCheckResult result = default;
        for (var i = 0; i < 5; i++)
        {
            var counts = new PersistedCounts
            {
                Outgoing = 500,                                  // flat depth...
                OldestOutgoing = t.AddSeconds(30 * i),           // ...but a head that keeps moving
                Handled = 1000 * (i + 1)
            };

            result = signals.Evaluate(AgentStatus.Running, AgentUri, counts, t.AddSeconds(30 * i));
        }

        ShouldNotReportStuckOutbox(result);
    }

    /// <summary>
    /// A store that does not report the head says nothing, rather than being guessed at. This is the common
    /// case: the timestamp column only exists when OutboxStaleTime is set.
    /// </summary>
    [Fact]
    public void an_unmeasured_head_stands_the_signal_down()
    {
        var signals = new DurabilityHealthSignals(Settings());
        var t = DateTimeOffset.UtcNow;

        HealthCheckResult result = default;
        for (var i = 0; i < 5; i++)
        {
            var counts = new PersistedCounts
            {
                Outgoing = 500,
                OldestOutgoing = null,      // NOT MEASURED
                Handled = 1000 * (i + 1)
            };

            result = signals.Evaluate(AgentStatus.Running, AgentUri, counts, t.AddSeconds(30 * i));
        }

        ShouldNotReportStuckOutbox(result);
    }

    [Fact]
    public void an_empty_outbox_is_not_reported()
    {
        var signals = new DurabilityHealthSignals(Settings());
        var t = DateTimeOffset.UtcNow;

        HealthCheckResult result = default;
        for (var i = 0; i < 5; i++)
        {
            // A head value with no rows behind it would be a contradiction, but assert on the depth gate
            // anyway: nothing pending is nothing to be stuck on
            var counts = new PersistedCounts { Outgoing = 0, OldestOutgoing = t.AddMinutes(-20), Handled = 5 };
            result = signals.Evaluate(AgentStatus.Running, AgentUri, counts, t.AddSeconds(30 * i));
        }

        ShouldNotReportStuckOutbox(result);
    }

    [Fact]
    public void one_poll_of_head_movement_clears_the_counter()
    {
        var signals = new DurabilityHealthSignals(Settings());
        var t = DateTimeOffset.UtcNow;
        var head = t.AddMinutes(-20);

        PersistedCounts CountsFor(DateTimeOffset oldest)
            => new() { Outgoing = 5, OldestOutgoing = oldest, Handled = 1 };

        // Three polls with a static head: one short of tripping the threshold
        for (var i = 0; i < 3; i++)
        {
            ShouldNotReportStuckOutbox(
                signals.Evaluate(AgentStatus.Running, AgentUri, CountsFor(head), t.AddSeconds(30 * i)));
        }

        // The head moves...
        var moved = head.AddMinutes(5);
        ShouldNotReportStuckOutbox(
            signals.Evaluate(AgentStatus.Running, AgentUri, CountsFor(moved), t.AddSeconds(120)));

        // ...so the next static poll starts counting from zero rather than tripping
        ShouldNotReportStuckOutbox(
            signals.Evaluate(AgentStatus.Running, AgentUri, CountsFor(moved), t.AddSeconds(150)));
    }


    /// <summary>
    /// A single wedged envelope at the head of an otherwise-flowing outbox is still a stuck envelope, and is
    /// reported. Recorded as intended behaviour rather than an accident.
    /// </summary>
    [Fact]
    public void one_wedged_envelope_at_the_head_of_a_draining_outbox_is_reported()
    {
        var signals = new DurabilityHealthSignals(Settings());
        var t = DateTimeOffset.UtcNow;
        var wedged = t.AddHours(-2);

        HealthCheckResult result = default;
        for (var i = 0; i < 5; i++)
        {
            var counts = new PersistedCounts
            {
                // Depth moves around, and the rest of the queue is clearly flowing...
                Outgoing = 500 - 50 * i,
                OldestOutgoing = wedged,    // ...but this one never leaves
                Handled = 1000 * (i + 1)
            };

            result = signals.Evaluate(AgentStatus.Running, AgentUri, counts, t.AddSeconds(30 * i));
        }

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description!.ShouldContain(TheMessage);
    }

    /// <summary>
    /// <see cref="PersistedCounts.Add" /> folds many stores into one reading, and the combining operation for
    /// a head timestamp is MIN — the earliest head across the fleet is the one that has waited longest.
    /// Unknown plus unknown stays unknown.
    /// </summary>
    [Fact]
    public void combining_counts_takes_the_earliest_measured_head()
    {
        var early = DateTimeOffset.UtcNow.AddHours(-3);
        var late = DateTimeOffset.UtcNow.AddMinutes(-1);

        var a = new PersistedCounts { Outgoing = 1, OldestOutgoing = late };
        a.Add(new PersistedCounts { Outgoing = 1, OldestOutgoing = early });
        a.OldestOutgoing.ShouldBe(early);

        // ...whichever side it arrives on
        var b = new PersistedCounts { Outgoing = 1, OldestOutgoing = early };
        b.Add(new PersistedCounts { Outgoing = 1, OldestOutgoing = late });
        b.OldestOutgoing.ShouldBe(early);

        // A store that measures nothing must not erase one that does
        var c = new PersistedCounts { Outgoing = 1, OldestOutgoing = early };
        c.Add(new PersistedCounts { Outgoing = 1, OldestOutgoing = null });
        c.OldestOutgoing.ShouldBe(early);

        var d = new PersistedCounts { Outgoing = 1, OldestOutgoing = null };
        d.Add(new PersistedCounts { Outgoing = 1, OldestOutgoing = early });
        d.OldestOutgoing.ShouldBe(early);

        // ...and unknown + unknown stays unknown rather than collapsing to a value
        var e = new PersistedCounts { Outgoing = 1 };
        e.Add(new PersistedCounts { Outgoing = 1 });
        e.OldestOutgoing.ShouldBeNull();
    }
}
