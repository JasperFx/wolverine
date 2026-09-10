using System.Diagnostics;
using CoreTests.Runtime;
using NSubstitute;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.ErrorHandling;
using Xunit;

namespace CoreTests.ErrorHandling;

public class RequeueContinuationTester
{
    [Fact]
    public async Task executing_just_puts_it_back_in_line_at_the_back_of_the_queue()
    {
        var envelope = ObjectMother.Envelope();

        var context = Substitute.For<IEnvelopeLifecycle>();
        context.Envelope.Returns(envelope);


        await RequeueContinuation.Instance.ExecuteAsync(context, new MockWolverineRuntime(), DateTime.Now, null);

        await context.Received(1).DeferAsync();
    }

    [Fact]
    public void requeue_continuation_with_delay_accepts_jitter()
    {
        var continuation = new RequeueContinuation(TimeSpan.FromSeconds(5));
        var strategy = new FixedMultiplierJitter(2.0);

        ((IJitterable)continuation).TrySetJitter(strategy).ShouldBeTrue();
    }

    [Fact]
    public void singleton_requeue_continuation_rejects_jitter()
    {
        var strategy = new FixedMultiplierJitter(2.0);

        ((IJitterable)RequeueContinuation.Instance).TrySetJitter(strategy).ShouldBeFalse();
    }

    [Fact]
    public async Task records_the_failed_attempt_so_the_next_attempt_can_link_to_it()
    {
        var envelope = ObjectMother.Envelope();

        var context = Substitute.For<IEnvelopeLifecycle>();
        context.Envelope.Returns(envelope);

        using var activity = new Activity("process").Start();

        await RequeueContinuation.Instance.ExecuteAsync(context, new MockWolverineRuntime(), DateTime.Now, activity);

        envelope.TryGetHeader(EnvelopeConstants.PreviousAttemptActivityIdKey, out var previous).ShouldBeTrue();
        previous.ShouldBe(activity.Id);
    }
}