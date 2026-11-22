using System.Text;
using Shouldly;
using Wolverine.Kafka.Internals;

namespace Wolverine.Kafka.Tests;

/// <summary>
/// GH-5772. <c>JsonOnlyMapper</c> (ReceiveRawJson / PublishRawJson) set the record key to <c>envelope.GroupId</c>
/// unconditionally. A dead-letter or retry-tier copy of a received record therefore lost its key (no GroupId) or,
/// with the default consumer group stamping, was keyed by the consumer group name, sending every copy to one
/// partition. A copy now keeps the key of the record it came from; a message the application sends keeps the
/// GroupId it set.
/// </summary>
public class kafka_copies_keep_their_key_5772
{
    [Fact]
    public async Task a_copy_of_a_received_record_keeps_its_key_over_the_stamped_consumer_group()
    {
        var mapper = buildMapper();
        var envelope = received(mapper, "customer-7");

        // What KafkaListener stamps by default (StampConsumerGroupIdOnEnvelope)
        envelope.GroupId = "my-consumer-group";

        var record = await mapper.CreateMessage(envelope);

        record.Key.ShouldBe("customer-7");
    }

    [Fact]
    public async Task a_copy_of_a_received_record_keeps_its_key_without_a_group_id()
    {
        var mapper = buildMapper();
        var envelope = received(mapper, "customer-7");

        var record = await mapper.CreateMessage(envelope);

        record.Key.ShouldBe("customer-7");
    }

    [Fact]
    public async Task a_copy_of_a_received_record_without_a_key_does_not_take_the_consumer_group_name()
    {
        var mapper = buildMapper();
        var envelope = received(mapper, null);
        envelope.GroupId = "my-consumer-group";

        var record = await mapper.CreateMessage(envelope);

        record.Key.ShouldNotBe("my-consumer-group");
    }

    [Fact]
    public async Task an_application_message_keeps_the_group_id_it_set()
    {
        var envelope = new Envelope { Data = Encoding.UTF8.GetBytes("{}"), GroupId = "group-1", PartitionKey = "pk-1" };

        var record = await buildMapper().CreateMessage(envelope);

        record.Key.ShouldBe("group-1");
    }

    [Fact]
    public async Task an_application_message_without_a_group_id_uses_its_partition_key()
    {
        var envelope = new Envelope { Data = Encoding.UTF8.GetBytes("{}"), PartitionKey = "pk-1" };

        var record = await buildMapper().CreateMessage(envelope);

        record.Key.ShouldBe("pk-1");
    }

    // What KafkaListener.buildEnvelope does with a record off the broker
    private static Envelope received(JsonOnlyMapper mapper, string? key)
    {
        var message = new Confluent.Kafka.Message<string, byte[]>
        {
            Key = key!,
            Value = Encoding.UTF8.GetBytes("{\"Color\":\"blue\"}")
        };
        var envelope = mapper.CreateEnvelope("copies-5772", message);
        envelope.TopicName = "copies-5772";
        envelope.Offset = 41;
        envelope.PartitionId = 2;

        return envelope;
    }

    private static JsonOnlyMapper buildMapper()
    {
        var topic = new KafkaTopic(new KafkaTransport(), "copies-5772", Configuration.EndpointRole.Application)
        {
            MessageType = typeof(ColorMessage)
        };

        return new JsonOnlyMapper(topic);
    }
}

/// <summary>
/// GH-5772. Retry tiers skipped every listener topic whose name merely contained ".retry.", so a user topic such
/// as <c>billing.retry.requests</c> silently got none. Only Wolverine's own tier topics are excluded now.
/// </summary>
public class kafka_retry_tiers_for_topics_named_retry_5772
{
    private static readonly TimeSpan[] Delays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)];

    [Fact]
    public void a_user_topic_named_like_a_retry_topic_gets_retry_tiers()
    {
        var transport = new KafkaTransport();
        transport.Topics["billing.retry.requests"].IsListener = true;
        transport.Topics["orders"].IsListener = true;

        transport.RetryTierSourceTopics(Delays).ShouldBe(["billing.retry.requests", "orders"], ignoreOrder: true);
    }

    [Fact]
    public void wolverine_retry_tier_topics_get_no_tiers_of_their_own()
    {
        var transport = new KafkaTransport();
        transport.Topics["orders"].IsListener = true;

        var tier = transport.Topics[KafkaRetryNaming.RetryTopicName("orders", Delays[0])];
        tier.IsListener = true;
        tier.RetryTierDelay = Delays[0];

        // A listener on a tier topic of another source, declared before the tiers are registered
        transport.Topics[KafkaRetryNaming.RetryTopicName("orders", Delays[1])].IsListener = true;

        transport.RetryTierSourceTopics(Delays).ShouldBe(["orders"]);
    }

    [Fact]
    public void the_dead_letter_and_system_topics_get_no_tiers()
    {
        var transport = new KafkaTransport();
        transport.Topics["orders"].IsListener = true;
        transport.Topics[transport.DeadLetterQueueTopicName].IsListener = true;
        transport.Topics[KafkaTopic.WolverineTopicsName].IsListener = true;

        transport.RetryTierSourceTopics(Delays).ShouldBe(["orders"]);
    }
}
