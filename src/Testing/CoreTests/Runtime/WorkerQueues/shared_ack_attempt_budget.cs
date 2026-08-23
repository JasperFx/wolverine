using JasperFx.Core;
using NSubstitute;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
/// GH-4012. <c>RetryBlock.MaximumAttempts</c> bounds retries WITHIN one <c>PostAsync</c>, but the
/// durable completion path stacks two retry blocks -- <c>DurableReceiver._completeBlock</c> ->
/// <c>Listener.CompleteAsync</c> -> <c>RabbitMqChannelCallback.Complete</c> -- so their budgets
/// multiplied to nine broker round trips for a single delivery, with neither block able to see the
/// other's count.
///
/// <c>Envelope.AckAttempts</c> rides the envelope so the layers share one budget. The increment
/// belongs to the innermost layer that actually issues the broker call; outer layers only check.
/// </summary>
public class shared_ack_attempt_budget
{
    /// <summary>
    /// Stands in for a transport whose own inner retry block spends the whole budget inside a
    /// single outer attempt -- which is exactly the RabbitMQ shape, and the case the multiplication
    /// bug lived in.
    /// </summary>
    private class BudgetBurningListener : IListener
    {
        private readonly int _burnPerCall;

        public BudgetBurningListener(int burnPerCall)
        {
            _burnPerCall = burnPerCall;
        }

        public int CompleteCallCount { get; private set; }

        public IHandlerPipeline? Pipeline => null;

        public ValueTask CompleteAsync(Envelope envelope)
        {
            CompleteCallCount++;
            envelope.AckAttempts += _burnPerCall;
            throw new TimeoutException("broker did not respond");
        }

        public ValueTask DeferAsync(Envelope envelope) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Uri Address { get; } = new("stub://one");
        public ValueTask StopAsync() => ValueTask.CompletedTask;
    }

    private static Envelope expiredEnvelopeFor(IListener listener)
    {
        // An already-expired envelope is the shortest route from ReceivedAsync into _completeBlock
        var envelope = ObjectMother.Envelope();
        envelope.DeliverBy = DateTimeOffset.UtcNow.Subtract(1.Minutes());
        envelope.Listener = listener;
        return envelope;
    }

    private static async Task<int> runAsync(int burnPerCall, int maximumAckAttempts)
    {
        var runtime = new MockWolverineRuntime();
        runtime.DurabilitySettings.MaximumAckAttempts = maximumAckAttempts;

        var pipeline = Substitute.For<IHandlerPipeline>();
        var receiver = new DurableReceiver(new StubEndpoint("one", new StubTransport()), runtime, pipeline);

        var listener = new BudgetBurningListener(burnPerCall);
        var envelope = expiredEnvelopeFor(listener);

        await receiver.ReceivedAsync(listener, envelope);
        await receiver.DrainAsync();

        return listener.CompleteCallCount;
    }

    [Fact]
    public async Task an_inner_layer_that_spends_the_whole_budget_stops_the_outer_retry_loop()
    {
        // One outer attempt burns the entire budget (the 3-inner-attempts-per-outer-attempt shape).
        // Before the shared budget, the outer block would retry its full MaximumAttempts on top,
        // giving 3 x 3 = 9 broker round trips. Now the second outer attempt sees a spent budget and
        // gives up.
        var calls = await runAsync(burnPerCall: 3, maximumAckAttempts: 3);

        calls.ShouldBe(1);
    }

    [Fact]
    public async Task a_transport_that_does_not_participate_keeps_its_existing_behavior()
    {
        // Nothing increments AckAttempts, so the guard never trips and the outer RetryBlock's own
        // MaximumAttempts governs exactly as it did before GH-4012. This is the compatibility case:
        // transports that settle directly in Listener.CompleteAsync are untouched.
        var calls = await runAsync(burnPerCall: 0, maximumAckAttempts: 3);

        calls.ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task the_budget_is_configurable()
    {
        // A budget of 1 means the very first outer retry finds it spent
        var calls = await runAsync(burnPerCall: 1, maximumAckAttempts: 1);

        calls.ShouldBe(1);
    }
}

/// <summary>
/// GH-4012 unit coverage for the counter itself.
/// </summary>
public class envelope_ack_attempt_budget
{
    [Fact]
    public void records_attempts_up_to_the_maximum_then_refuses()
    {
        var envelope = ObjectMother.Envelope();

        envelope.TryRecordAckAttempt(3).ShouldBeTrue();
        envelope.AckAttempts.ShouldBe(1);

        envelope.TryRecordAckAttempt(3).ShouldBeTrue();
        envelope.TryRecordAckAttempt(3).ShouldBeTrue();
        envelope.AckAttempts.ShouldBe(3);

        envelope.TryRecordAckAttempt(3).ShouldBeFalse();

        // A refused attempt must not keep incrementing -- the counter is also what the log line
        // reports, and an unbounded value there would be noise
        envelope.AckAttempts.ShouldBe(3);
    }

    [Fact]
    public void a_maximum_of_zero_refuses_immediately()
    {
        var envelope = ObjectMother.Envelope();

        envelope.TryRecordAckAttempt(0).ShouldBeFalse();
        envelope.AckAttempts.ShouldBe(0);
    }
}
