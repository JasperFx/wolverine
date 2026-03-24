using Confluent.Kafka;
using Wolverine.Configuration;

namespace Wolverine.Kafka;

public class KafkaTopicGroupListenerConfiguration : ListenerConfiguration<KafkaTopicGroupListenerConfiguration, KafkaTopicGroup>
{
    public KafkaTopicGroupListenerConfiguration(KafkaTopicGroup endpoint) : base(endpoint)
    {
    }

    /// <summary>
    /// Configure the consumer config for this topic group. This overrides the default
    /// settings at the transport level.
    /// </summary>
    /// <param name="configuration"></param>
    /// <returns></returns>
    public KafkaTopicGroupListenerConfiguration ConfigureConsumer(Action<ConsumerConfig> configuration)
    {
        add(group =>
        {
            var config = new ConsumerConfig();
            configuration(config);
            group.ConsumerConfig = config;
        });
        return this;
    }

    /// <summary>
    /// Disables stamping of the Kafka consumer group ID onto the GroupId property
    /// of each incoming envelope. Use this when the consumer group name is not
    /// meaningful as envelope metadata (e.g. when using PropagateGroupIdToPartitionKey).
    /// </summary>
    /// <returns></returns>
    public KafkaTopicGroupListenerConfiguration DisableConsumerGroupIdStamping()
    {
        add(group => group.StampConsumerGroupIdOnEnvelope = false);
        return this;
    }

    /// <summary>
    /// Enable native dead letter queue support for this listener group.
    /// </summary>
    /// <returns></returns>
    public KafkaTopicGroupListenerConfiguration EnableNativeDeadLetterQueue()
    {
        add(group => group.NativeDeadLetterQueueEnabled = true);
        return this;
    }
}
