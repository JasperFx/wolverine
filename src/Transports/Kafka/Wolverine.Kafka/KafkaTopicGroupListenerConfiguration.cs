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
    /// Enable native dead letter queue support for this listener group.
    /// </summary>
    /// <returns></returns>
    public KafkaTopicGroupListenerConfiguration EnableNativeDeadLetterQueue()
    {
        add(group => group.NativeDeadLetterQueueEnabled = true);
        return this;
    }
}
