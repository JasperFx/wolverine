using Shouldly;
using Wolverine.Configuration;
using Wolverine.Kafka.Internals;
using Wolverine.Transports;

namespace Wolverine.Kafka.Tests;

/// <summary>
/// GH-5926. A buffered Kafka listener completes each record's offset once the record finishes, not when the buffered
/// receiver queues it. buffered_listener_redelivers_unfinished_messages proves the end to end result against a broker;
/// these pin the two pieces of wiring it depends on.
/// </summary>
public class kafka_buffered_listener_settles_after_handling_5926
{
    [Fact]
    public void the_kafka_listener_is_settled_after_handling()
    {
        typeof(KafkaListener).IsAssignableTo(typeof(ISettlesAfterHandlingListener)).ShouldBeTrue();
    }

    [Fact]
    public void a_buffered_topic_does_not_stop_its_consumer_for_back_pressure()
    {
        // Stopping the consumer would hand the records still in the buffer back to the group while this node works
        // through them. The buffered receiver's bounded block holds the consume loop back instead
        var topic = new KafkaTransport().Topics["back-pressure-5926"];
        topic.Mode = EndpointMode.BufferedInMemory;

        topic.ShouldEnforceBackPressure().ShouldBeFalse();
    }

    [Fact]
    public void a_buffered_topic_bounds_its_queue_by_its_buffering_limits()
    {
        var topic = new KafkaTransport().Topics["buffer-bound-5926"];
        topic.Mode = EndpointMode.BufferedInMemory;
        topic.BufferedReceiverCapacity.ShouldBe(topic.BufferingLimits.Maximum);

        topic.BufferingLimits = new BufferingLimits(250, 100);
        topic.BufferedReceiverCapacity.ShouldBe(250);
    }

    [Fact]
    public void a_durable_topic_still_uses_back_pressure()
    {
        var topic = new KafkaTransport().Topics["durable-back-pressure-5926"];
        topic.Mode = EndpointMode.Durable;

        topic.ShouldEnforceBackPressure().ShouldBeTrue();
    }

    [Fact]
    public void an_inline_topic_never_used_back_pressure()
    {
        var topic = new KafkaTransport().Topics["inline-back-pressure-5926"];
        topic.Mode = EndpointMode.Inline;

        topic.ShouldEnforceBackPressure().ShouldBeFalse();
    }
}
