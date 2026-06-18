using Confluent.Kafka;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Kafka.Internals;

namespace Wolverine.Kafka.Tests;

// GH-3149: opt-in exactly-once building blocks — idempotent producer + read_committed isolation.
public class kafka_eos_building_blocks
{
    [Fact]
    public void transport_idempotent_producer()
    {
        var transport = new KafkaTransport();
        new KafkaTransportExpression(transport, new WolverineOptions()).UseIdempotentProducer();

        transport.ProducerConfig.EnableIdempotence.ShouldBe(true);
    }

    [Fact]
    public void transport_read_committed()
    {
        var transport = new KafkaTransport();
        new KafkaTransportExpression(transport, new WolverineOptions()).UseReadCommitted();

        transport.ConsumerConfig.IsolationLevel.ShouldBe(IsolationLevel.ReadCommitted);
    }

    [Fact]
    public void listener_read_committed()
    {
        var transport = new KafkaTransport();
        var topic = transport.Topics["events"];
        var config = new KafkaListenerConfiguration(topic);
        config.UseReadCommitted();
        ((IDelayedEndpointConfiguration)config).Apply();

        topic.ConsumerConfig!.IsolationLevel.ShouldBe(IsolationLevel.ReadCommitted);
    }

    [Fact]
    public void subscriber_idempotent_producer()
    {
        var transport = new KafkaTransport();
        var topic = transport.Topics["events"];
        var config = new KafkaSubscriberConfiguration(topic);
        config.UseIdempotentProducer();
        ((IDelayedEndpointConfiguration)config).Apply();

        topic.ProducerConfig!.EnableIdempotence.ShouldBe(true);
    }

    [Fact]
    public void defaults_are_unchanged()
    {
        // Opt-in: a vanilla transport touches neither setting.
        var transport = new KafkaTransport();
        transport.ProducerConfig.EnableIdempotence.ShouldBeNull();
        transport.ConsumerConfig.IsolationLevel.ShouldBeNull();
    }
}
