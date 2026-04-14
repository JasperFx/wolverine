using System.Diagnostics;
using CoreTests.Runtime;
using NSubstitute;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.ErrorHandling;
using Xunit;

namespace CoreTests.ErrorHandling;

public class RetryNowContinuationTester
{
    [Fact]
    public async Task just_calls_through_to_the_context_pipeline_to_do_it_again()
    {
        var continuation = RetryInlineContinuation.Instance;

        var envelope = ObjectMother.Envelope();
        envelope.Attempts = 1;

        var context = Substitute.For<IEnvelopeLifecycle>();
        context.Envelope.Returns(envelope);

        await continuation.ExecuteAsync(context, new MockWolverineRuntime(), DateTimeOffset.Now, new Activity("process"));

        await context.Received(1).RetryExecutionNowAsync();
    }

    [Fact]
    public async Task inline_retry_with_jitter_uses_jittered_delay_floor()
    {
        // Strategy always returns 2× base so we can verify it was used.
        var strategy = new FixedMultiplierJitter(2.0);
        var baseDelay = TimeSpan.FromMilliseconds(50);

        var continuation = new RetryInlineContinuation(baseDelay);
        ((IJitterable)continuation).TrySetJitter(strategy).ShouldBeTrue();

        var envelope = ObjectMother.Envelope();
        envelope.Attempts = 1;

        var context = Substitute.For<IEnvelopeLifecycle>();
        context.Envelope.Returns(envelope);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await continuation.ExecuteAsync(context, new MockWolverineRuntime(), DateTimeOffset.Now, new Activity("process"));
        sw.Stop();

        // Actual wait is ~100ms (2× 50ms); use a generous lower bound to stay stable under load.
        sw.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(90));
        await context.Received(1).RetryExecutionNowAsync();
    }

    [Fact]
    public void singleton_retry_inline_continuation_rejects_jitter()
    {
        var strategy = new FixedMultiplierJitter(2.0);

        ((IJitterable)RetryInlineContinuation.Instance).TrySetJitter(strategy).ShouldBeFalse();
    }
}
