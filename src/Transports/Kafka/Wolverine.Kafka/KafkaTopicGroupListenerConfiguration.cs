using Confluent.Kafka;
using Confluent.Kafka.Admin;
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

    /// <summary>
    /// Fine tune the TopicSpecification for all topics in this group if they are being created by Wolverine.
    /// Use this to set partition count, replication factor, etc. uniformly across all topics.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public KafkaTopicGroupListenerConfiguration Specification(Action<TopicSpecification> configure)
    {
        if (configure == null) throw new ArgumentNullException(nameof(configure));
        add(group => group.SpecificationConfig = (_, spec) => configure(spec));
        return this;
    }

    /// <summary>
    /// Fine tune the TopicSpecification per topic in this group if they are being created by Wolverine.
    /// The first parameter is the topic name, allowing per-topic configuration such as different partition counts.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public KafkaTopicGroupListenerConfiguration Specification(Action<string, TopicSpecification> configure)
    {
        if (configure == null) throw new ArgumentNullException(nameof(configure));
        add(group => group.SpecificationConfig = configure);
        return this;
    }

    /// <summary>
    /// If you need to do anything "special" to create topics at runtime with Wolverine,
    /// this overrides the simple logic that Wolverine uses and replaces it with whatever
    /// you need to do having full access to the Kafka IAdminClient and the topic name.
    /// Called once per topic in the group.
    /// </summary>
    /// <param name="creation"></param>
    /// <returns></returns>
    public KafkaTopicGroupListenerConfiguration TopicCreation(Func<IAdminClient, string, Task> creation)
    {
        if (creation == null) throw new ArgumentNullException(nameof(creation));
        add(group => group.CreateTopicFunc = creation);
        return this;
    }
}
