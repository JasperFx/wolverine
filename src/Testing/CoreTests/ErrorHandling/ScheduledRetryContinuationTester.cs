using System.Diagnostics;
using CoreTests.Runtime;
using NSubstitute;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.ErrorHandling;
using Xunit;

namespace CoreTests.ErrorHandling;

public class ScheduledRetryContinuationTester
{
    [Fact]
    public async Task applies_jittered_delay_when_scheduling()
    {
        // Multiplier of 3: base 10s → effective 30s.
        var strategy = new FixedMultiplierJitter(3.0);
        var baseDelay = TimeSpan.FromSeconds(10);

        var continuation = new ScheduledRetryContinuation(baseDelay);
        ((IJitterable)continuation).TrySetJitter(strategy).ShouldBeTrue();

        var envelope = ObjectMother.Envelope();
        envelope.Attempts = 1;

        var lifecycle = Substitute.For<IEnvelopeLifecycle>();
        lifecycle.Envelope.Returns(envelope);

        var now = new DateTimeOffset(2026, 4, 13, 12, 0, 0, TimeSpan.Zero);

        await continuation.ExecuteAsync(lifecycle, new MockWolverineRuntime(), now, new Activity("process"));

        await lifecycle.Received(1).ReScheduleAsync(now.AddSeconds(30));
    }

    [Fact]
    public async Task uses_base_delay_when_no_jitter_configured()
    {
        var baseDelay = TimeSpan.FromSeconds(10);
        var continuation = new ScheduledRetryContinuation(baseDelay);

        var envelope = ObjectMother.Envelope();
        envelope.Attempts = 1;

        var lifecycle = Substitute.For<IEnvelopeLifecycle>();
        lifecycle.Envelope.Returns(envelope);

        var now = new DateTimeOffset(2026, 4, 13, 12, 0, 0, TimeSpan.Zero);

        await continuation.ExecuteAsync(lifecycle, new MockWolverineRuntime(), now, new Activity("process"));

        await lifecycle.Received(1).ReScheduleAsync(now.AddSeconds(10));
    }

    [Fact]
    public async Task records_the_failed_attempt_so_the_next_attempt_can_link_to_it()
    {
        var continuation = new ScheduledRetryContinuation(TimeSpan.FromSeconds(10));

        var envelope = ObjectMother.Envelope();
        envelope.Attempts = 1;

        var lifecycle = Substitute.For<IEnvelopeLifecycle>();
        lifecycle.Envelope.Returns(envelope);

        using var activity = new Activity("process").Start();

        await continuation.ExecuteAsync(lifecycle, new MockWolverineRuntime(), DateTimeOffset.UtcNow, activity);

        envelope.TryGetHeader(EnvelopeConstants.PreviousAttemptActivityIdKey, out var previous).ShouldBeTrue();
        previous.ShouldBe(activity.Id);
    }

    [Fact]
    public async Task records_nothing_when_there_is_no_activity()
    {
        var continuation = new ScheduledRetryContinuation(TimeSpan.FromSeconds(10));

        var envelope = ObjectMother.Envelope();
        envelope.Attempts = 1;

        var lifecycle = Substitute.For<IEnvelopeLifecycle>();
        lifecycle.Envelope.Returns(envelope);

        await continuation.ExecuteAsync(lifecycle, new MockWolverineRuntime(), DateTimeOffset.UtcNow, null);

        envelope.TryGetHeader(EnvelopeConstants.PreviousAttemptActivityIdKey, out _).ShouldBeFalse();
    }
}
