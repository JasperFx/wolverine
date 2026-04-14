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
}
