using JasperFx.Core;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Wolverine.Persistence.Durability;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
/// GH-4012 item 4. The redeliver -> dedupe -> re-ack loop: a delivery that can never be settled, an inbox
/// that deduplicates it on arrival, a settle that fails again, and a broker that delivers it once more.
/// </summary>
/// <remarks>
/// Envelope.AckAttempts (item 1) cannot bound this and never could -- every redelivery arrives as a brand
/// new envelope with a fresh counter. Only the broker's own count survives that boundary, which is the
/// entire reason Envelope.BrokerDeliveryCount exists.
/// </remarks>
public class broker_redelivery_bounding_4012
{
    private class UnsettleableListener : IListener, ISupportDeadLetterQueue
    {
        public int CompleteCallCount { get; private set; }
        public int DeadLetterCallCount { get; private set; }

        public bool NativeDeadLetterQueueEnabled => true;

        public IHandlerPipeline? Pipeline => null;

        public ValueTask CompleteAsync(Envelope envelope)
        {
            CompleteCallCount++;
            return ValueTask.CompletedTask;
        }

        public Task MoveToErrorsAsync(Envelope envelope, Exception exception)
        {
            DeadLetterCallCount++;
            return Task.CompletedTask;
        }

        public ValueTask DeferAsync(Envelope envelope) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Uri Address { get; } = new("stub://redelivery");
        public ValueTask StopAsync() => ValueTask.CompletedTask;
    }

    private static DurableReceiver receiverFor(int limit)
    {
        var runtime = new MockWolverineRuntime();
        runtime.DurabilitySettings.MaximumBrokerRedeliveries = limit;

        return new DurableReceiver(new StubEndpoint("one", new StubTransport()), runtime,
            Substitute.For<IHandlerPipeline>());
    }

    private static Envelope envelopeWith(int? brokerDeliveryCount)
    {
        var envelope = ObjectMother.Envelope();
        envelope.BrokerDeliveryCount = brokerDeliveryCount;
        return envelope;
    }

    [Fact]
    public void a_delivery_count_past_the_limit_is_exhausted()
    {
        receiverFor(3).HasExhaustedBrokerRedeliveries(envelopeWith(4)).ShouldBeTrue();
    }

    [Fact]
    public void the_limit_itself_is_not_past_it()
    {
        // Off-by-one matters: a message delivered exactly the permitted number of times still gets to run
        receiverFor(3).HasExhaustedBrokerRedeliveries(envelopeWith(3)).ShouldBeFalse();
    }

    [Fact]
    public void the_bound_is_off_by_default()
    {
        // Zero leaves the broker's own limit in charge and changes nothing, which is what makes this
        // additive for every existing application
        receiverFor(0).HasExhaustedBrokerRedeliveries(envelopeWith(500)).ShouldBeFalse();
    }

    [Fact]
    public void a_transport_with_no_delivery_count_is_unaffected()
    {
        // RabbitMQ on plain nack-requeue, core NATS, Redis Streams -- nothing to bound, so nothing happens.
        // Guessing a count here is exactly what the RabbitMQ mapper deliberately refuses to do
        receiverFor(3).HasExhaustedBrokerRedeliveries(envelopeWith(null)).ShouldBeFalse();
    }

    /// <summary>
    /// The behaviour, not just the predicate. This is the loop actually turning: the inbox rejects the
    /// envelope as a duplicate, and instead of settling it again -- the settle that keeps failing, which is
    /// what keeps the broker redelivering -- it goes to the dead letter queue.
    /// </summary>
    [Fact]
    public async Task an_over_delivered_duplicate_is_dead_lettered_instead_of_re_acked()
    {
        var runtime = new MockWolverineRuntime();
        runtime.DurabilitySettings.MaximumBrokerRedeliveries = 3;

        var receiver = new DurableReceiver(new StubEndpoint("one", new StubTransport()), runtime,
            Substitute.For<IHandlerPipeline>());

        var envelope = envelopeWith(4);
        var listener = new UnsettleableListener();

        runtime.Storage.Inbox.StoreIncomingAsync(envelope)
            .Throws(new DuplicateIncomingEnvelopeException(envelope));

        await receiver.ReceivedAsync(listener, envelope);
        await receiver.DrainAsync();

        listener.DeadLetterCallCount.ShouldBe(1);
        listener.CompleteCallCount.ShouldBe(0);
    }

    /// <summary>
    /// The control. Same path, same duplicate, a delivery count inside the limit -- and the existing
    /// settle-the-duplicate behaviour is untouched. Without this the test above would pass just as well
    /// if the bound fired on every duplicate.
    /// </summary>
    [Fact]
    public async Task a_duplicate_within_the_limit_is_still_settled_the_old_way()
    {
        var runtime = new MockWolverineRuntime();
        runtime.DurabilitySettings.MaximumBrokerRedeliveries = 3;

        var receiver = new DurableReceiver(new StubEndpoint("one", new StubTransport()), runtime,
            Substitute.For<IHandlerPipeline>());

        var envelope = envelopeWith(2);
        var listener = new UnsettleableListener();

        runtime.Storage.Inbox.StoreIncomingAsync(envelope)
            .Throws(new DuplicateIncomingEnvelopeException(envelope));

        await receiver.ReceivedAsync(listener, envelope);
        await receiver.DrainAsync();

        listener.CompleteCallCount.ShouldBe(1);
        listener.DeadLetterCallCount.ShouldBe(0);
    }

    [Fact]
    public void the_default_setting_is_off()
    {
        new DurabilitySettings().MaximumBrokerRedeliveries.ShouldBe(0);
    }
}
