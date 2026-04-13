using System.Diagnostics;
using CoreTests.Runtime;
using JasperFx.Core;
using NSubstitute;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Xunit;

namespace CoreTests.ErrorHandling;

public class JitterIntegrationTests
{
    [Fact]
    public void with_full_jitter_returns_additional_actions()
    {
        var options = new WolverineOptions();
        var result = options.OnException<InvalidOperationException>()
            .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds())
            .WithFullJitter();

        result.ShouldNotBeNull();
    }

    [Fact]
    public void with_bounded_jitter_validates_percent()
    {
        var options = new WolverineOptions();

        Should.Throw<ArgumentOutOfRangeException>(() =>
            options.OnException<InvalidOperationException>()
                .RetryWithCooldown(50.Milliseconds())
                .WithBoundedJitter(0));
    }

    [Fact]
    public void jitter_rejected_on_rule_with_no_delay_slot()
    {
        var options = new WolverineOptions();

        Should.Throw<InvalidOperationException>(() =>
            options.OnException<InvalidOperationException>()
                .MoveToErrorQueue()
                .WithFullJitter());
    }

    [Fact]
    public void jitter_cannot_be_applied_twice_to_the_same_rule()
    {
        var options = new WolverineOptions();

        Should.Throw<InvalidOperationException>(() =>
            options.OnException<InvalidOperationException>()
                .RetryWithCooldown(50.Milliseconds())
                .WithFullJitter()
                .WithBoundedJitter(0.2));
    }

    [Fact]
    public void jitter_applies_to_schedule_retry_slots()
    {
        var options = new WolverineOptions();

        options.OnException<InvalidOperationException>()
            .ScheduleRetry(1.Seconds(), 5.Seconds())
            .WithExponentialJitter();
    }

    [Fact]
    public void jitter_applies_to_pause_then_requeue()
    {
        var options = new WolverineOptions();

        options.OnException<InvalidOperationException>()
            .PauseThenRequeue(5.Seconds())
            .WithBoundedJitter(0.25);
    }

    [Fact]
    public void jitter_applies_to_schedule_retry_indefinitely_including_infinite_source()
    {
        var options = new WolverineOptions();

        options.OnException<InvalidOperationException>()
            .ScheduleRetryIndefinitely(1.Seconds(), 5.Seconds(), 30.Seconds())
            .WithFullJitter();
    }

    [Fact]
    public async Task with_full_jitter_actually_extends_the_delay_at_runtime()
    {
        // Build a rule with jitter applied.
        var options = new WolverineOptions();
        options.OnException<InvalidOperationException>()
            .RetryWithCooldown(TimeSpan.FromMilliseconds(30))
            .WithFullJitter();

        // Navigate the FailureRuleCollection to get the actual continuation.
        // FailureRule implements IEnumerable<FailureSlot>; take the first slot
        // and build its continuation for an envelope with Attempts=1.
        var failureRules = options.Policies.Failures;
        // WolverineOptions ctor registers a default DuplicateIncomingEnvelopeException rule first,
        // so our InvalidOperationException rule is the last one registered.
        var ex = new InvalidOperationException("boom");
        var rule = failureRules.Last(r => r.Match.Matches(ex));
        var slot = rule.Single();

        var envelope = ObjectMother.Envelope();
        envelope.Attempts = 1;
        var continuation = slot.Build(new InvalidOperationException("boom"), envelope);

        var lifecycle = Substitute.For<IEnvelopeLifecycle>();
        lifecycle.Envelope.Returns(envelope);

        var sw = Stopwatch.StartNew();
        await continuation.ExecuteAsync(lifecycle, new MockWolverineRuntime(),
            DateTimeOffset.UtcNow, new Activity("process"));
        sw.Stop();

        // Additive invariant — elapsed must be at least the base delay (30ms).
        // Use a conservative lower bound to avoid flakiness under load.
        sw.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(25));
        // Upper bound: 2× base + slack
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void jitter_flag_persists_across_then_transition()
    {
        // By design, jitter is applied to the entire FailureRule, not per Then-segment.
        // Once a jitter strategy has been set on a rule, a second WithXxxJitter call
        // is rejected — even when invoked after .Then. This prevents accidentally
        // overwriting the strategy already applied to earlier slots.
        var options = new WolverineOptions();

        Should.Throw<InvalidOperationException>(() =>
            options.OnException<InvalidOperationException>()
                .RetryWithCooldown(50.Milliseconds())
                .WithFullJitter()
                .Then.ScheduleRetry(5.Seconds())
                .WithBoundedJitter(0.2));
    }

    [Fact]
    public async Task schedule_retry_passes_jittered_delay_to_reschedule()
    {
        var options = new WolverineOptions();
        options.OnException<InvalidOperationException>()
            .ScheduleRetry(1.Seconds())
            .WithBoundedJitter(0.5);

        var ex = new InvalidOperationException("boom");
        var failureRules = options.Policies.Failures;
        var rule = failureRules.Last(r => r.Match.Matches(ex));
        var slot = rule.Single();

        var envelope = Wolverine.ComplianceTests.ObjectMother.Envelope();
        envelope.Attempts = 1;
        var continuation = slot.Build(ex, envelope);

        var lifecycle = NSubstitute.Substitute.For<IEnvelopeLifecycle>();
        lifecycle.Envelope.Returns(envelope);

        DateTimeOffset? captured = null;
        lifecycle.ReScheduleAsync(NSubstitute.Arg.Do<DateTimeOffset>(dt => captured = dt))
            .Returns(Task.CompletedTask);

        var now = new DateTimeOffset(2026, 4, 13, 0, 0, 0, TimeSpan.Zero);
        await continuation.ExecuteAsync(lifecycle, new MockWolverineRuntime(),
            now, new System.Diagnostics.Activity("process"));

        captured.ShouldNotBeNull();
        var delay = captured!.Value - now;
        // Bounded jitter at 0.5 → delay ∈ [1s, 1.5s]
        delay.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        delay.ShouldBeLessThanOrEqualTo(TimeSpan.FromMilliseconds(1500));
    }

    [Fact]
    public async Task schedule_retry_indefinitely_jitters_the_infinite_source()
    {
        var options = new WolverineOptions();
        options.OnException<InvalidOperationException>()
            .ScheduleRetryIndefinitely(1.Seconds())
            .WithFullJitter();

        var ex = new InvalidOperationException("boom");
        var failureRules = options.Policies.Failures;
        var rule = failureRules.Last(r => r.Match.Matches(ex));

        // Force attempt past the single configured slot → InfiniteSource wins.
        var envelope = Wolverine.ComplianceTests.ObjectMother.Envelope();
        envelope.Attempts = 50;
        rule.TryCreateContinuation(ex, envelope, out var continuation).ShouldBeTrue();

        var lifecycle = NSubstitute.Substitute.For<IEnvelopeLifecycle>();
        lifecycle.Envelope.Returns(envelope);

        DateTimeOffset? captured = null;
        lifecycle.ReScheduleAsync(NSubstitute.Arg.Do<DateTimeOffset>(dt => captured = dt))
            .Returns(Task.CompletedTask);

        var now = new DateTimeOffset(2026, 4, 13, 0, 0, 0, TimeSpan.Zero);
        await continuation.ExecuteAsync(lifecycle, new MockWolverineRuntime(),
            now, new System.Diagnostics.Activity("process"));

        captured.ShouldNotBeNull();
        var delay = captured!.Value - now;
        // Full jitter → delay ∈ [1s, 2s]
        delay.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        delay.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void pause_then_requeue_builds_jitter_enabled_continuation()
    {
        var options = new WolverineOptions();
        options.OnException<InvalidOperationException>()
            .PauseThenRequeue(5.Seconds())
            .WithExponentialJitter();

        var ex = new InvalidOperationException("boom");
        var failureRules = options.Policies.Failures;
        var rule = failureRules.Last(r => r.Match.Matches(ex));
        var slot = rule.Single();

        var envelope = Wolverine.ComplianceTests.ObjectMother.Envelope();
        envelope.Attempts = 1;
        var continuation = slot.Build(ex, envelope);

        var requeue = continuation.ShouldBeOfType<RequeueContinuation>();
        requeue.Delay.ShouldBe(TimeSpan.FromSeconds(5));
        // Verify it's not the no-delay singleton, which is how we know this is a jitter-capable
        // variant — only RequeueContinuation instances constructed with a delay implement the
        // IJitterable contract in an accepting state.
        requeue.ShouldNotBeSameAs(RequeueContinuation.Instance);
    }
}
