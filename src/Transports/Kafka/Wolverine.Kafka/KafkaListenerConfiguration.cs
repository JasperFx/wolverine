using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Wolverine.Configuration;
using Wolverine.Kafka.Internals;

namespace Wolverine.Kafka;

public class KafkaListenerConfiguration : InteroperableListenerConfiguration<KafkaListenerConfiguration, KafkaTopic, IKafkaEnvelopeMapper, KafkaEnvelopeMapper>
{
    public KafkaListenerConfiguration(KafkaTopic endpoint) : base(endpoint)
    {
    }

    public KafkaListenerConfiguration(Func<KafkaTopic> source) : base(source)
    {
    }

    /// <summary>
    /// Fine tune the TopicSpecification for this Kafka Topic if it is being created by Wolverine
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public KafkaListenerConfiguration Specification(Action<TopicSpecification> configure)
    {
        if (configure == null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        add(topic => configure(topic.Specification));
        return this;
    }

    /// <summary>
    /// If you need to do anything "special" to create topics at runtime with Wolverine,
    /// this overrides the simple logic that Wolverine uses and replaces
    /// it with whatever you need to do having full access to the Kafka IAdminClient
    /// and the Wolverine KafkaTopic configuration
    /// </summary>
    /// <param name="creation"></param>
    /// <returns></returns>
    public KafkaListenerConfiguration TopicCreation(Func<IAdminClient, KafkaTopic, Task> creation)
    {
        if (creation == null)
        {
            throw new ArgumentNullException(nameof(creation));
        }

        add(topic => topic.CreateTopicFunc = creation);
        return this;
    }

    /// <summary>
    /// Configure this endpoint to receive messages of type T from
    /// JSON message bodies. This option maybe be necessary to receive
    /// messages from non-Wolverine applications
    /// </summary>
    /// <param name="options"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public KafkaListenerConfiguration ReceiveRawJson<T>(JsonSerializerOptions? options = null)
    {
        return ReceiveRawJson(typeof(T));
    }

    /// <summary>
    /// Configure this endpoint to receive messages of the message typ from
    /// JSON message bodies. This option maybe be necessary to receive
    /// messages from non-Wolverine applications
    /// </summary>
    /// <param name="messageType"></param>
    /// <param name="options"></param>
    /// <returns></returns>
    public KafkaListenerConfiguration ReceiveRawJson(Type messageType, JsonSerializerOptions? options = null)
    {
        DefaultIncomingMessage(messageType);
        return UseInterop((e, _) => new JsonOnlyMapper(e, options ?? new()));
    }

    /// <summary>
    /// Configure the consumer config for only this topic. This overrides the default
    /// settings at the transport level. This is not combinatorial with the parent configuration
    /// and overwrites all ConsumerConfig from the parent
    /// </summary>
    /// <param name="configuration"></param>
    /// <returns></returns>
    public KafkaListenerConfiguration ConfigureConsumer(Action<ConsumerConfig> configuration)
    {
        add(topic =>
        {
            var config = new ConsumerConfig();
            configuration(config);

            topic.ConsumerConfig = config;
        });
        return this;
    }

    /// <summary>
    /// Enable "at least once" delivery semantics for this topic. When enabled,
    /// Kafka offsets are stored only after message processing completes (for Inline mode)
    /// or after the message is persisted to the database inbox (for Durable mode).
    /// 
    /// This setting automatically sets EnableAutoOffsetStore=false on the consumer config.
    /// For best results, use with ProcessInline() or UseDurableInbox().
    /// 
    /// Note: BufferedInMemory mode does NOT provide at-least-once guarantees even with this setting.
    /// </summary>
    /// <returns></returns>
    public KafkaListenerConfiguration EnableAtLeastOnceDelivery()
    {
        add(topic =>
        {
            topic.EnableAtLeastOnceDelivery = true;
        });
        return this;
    }
}