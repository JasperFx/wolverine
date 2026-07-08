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
    public void subscriber_idempotent_producer_inherits_sasl_ssl_settings_from_parent_transport()
    {
        // UseIdempotentProducer() builds a fresh per-topic ProducerConfig containing only
        // EnableIdempotence, the same pattern as the per-topic ConsumerConfig builders above.
        // GetEffectiveProducerConfig() previously only backfilled BootstrapServers from the parent,
        // silently dropping SecurityProtocol/SaslMechanism/SaslUsername/SaslPassword.
        var transport = new KafkaTransport();
        transport.ProducerConfig.SecurityProtocol = SecurityProtocol.SaslSsl;
        transport.ProducerConfig.SaslMechanism = SaslMechanism.Plain;
        transport.ProducerConfig.SaslUsername = "api-key";
        transport.ProducerConfig.SaslPassword = "api-secret";

        var topic = transport.Topics["events"];
        var config = new KafkaSubscriberConfiguration(topic);
        config.UseIdempotentProducer();
        ((IDelayedEndpointConfiguration)config).Apply();

        var effective = topic.GetEffectiveProducerConfig();
        effective.SecurityProtocol.ShouldBe(SecurityProtocol.SaslSsl);
        effective.SaslMechanism.ShouldBe(SaslMechanism.Plain);
        effective.SaslUsername.ShouldBe("api-key");
        effective.SaslPassword.ShouldBe("api-secret");
    }

    [Fact]
    public void subscriber_idempotent_producer_does_not_override_explicitly_set_sasl_username()
    {
        var transport = new KafkaTransport();
        transport.ProducerConfig.SaslUsername = "parent-key";

        var topic = transport.Topics["events"];
        var config = new KafkaSubscriberConfiguration(topic);
        config.ConfigureProducer(c => c.SaslUsername = "topic-key");
        config.UseIdempotentProducer();
        ((IDelayedEndpointConfiguration)config).Apply();

        var effective = topic.GetEffectiveProducerConfig();
        effective.SaslUsername.ShouldBe("topic-key");
        effective.EnableIdempotence.ShouldBe(true);
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
