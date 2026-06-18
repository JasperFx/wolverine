using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
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
    /// Choose how this listener commits consumer offsets back to Kafka. Defaults to
    /// <see cref="CommitMode.StoreThenAutoFlush"/> (non-blocking, idiomatic high throughput). See GH-3150.
    /// </summary>
    public KafkaListenerConfiguration CommitOffsets(CommitMode mode)
    {
        add(topic => topic.CommitMode = mode);
        return this;
    }

    /// <summary>
    /// Have Wolverine commit the contiguous offset watermark after every <paramref name="count"/>
    /// successfully processed messages. Never commits ahead of the lowest in-flight offset.
    /// </summary>
    public KafkaListenerConfiguration CommitOffsetsAfterCount(int count)
    {
        add(topic =>
        {
            topic.CommitMode = CommitMode.BatchCount;
            topic.CommitBatchCount = count;
        });
        return this;
    }

    /// <summary>
    /// Have Wolverine commit the contiguous offset watermark once at least <paramref name="interval"/>
    /// has elapsed since the previous commit. Never commits ahead of the lowest in-flight offset.
    /// </summary>
    public KafkaListenerConfiguration CommitOffsetsAfterInterval(TimeSpan interval)
    {
        add(topic =>
        {
            topic.CommitMode = CommitMode.BatchInterval;
            topic.CommitBatchInterval = interval;
        });
        return this;
    }
    
    /// <summary>
    /// Configures circuit breaker behavior for this Kafka listener.
    /// </summary>
    /// <param name="configure">
    /// Optional configuration action for <see cref="CircuitBreakerOptions"/>.
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public KafkaListenerConfiguration CircuitBreaker(Action<CircuitBreakerOptions> configure)
    {
        if (configure == null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        add(topic =>
        {
            topic.CircuitBreakerOptions = new CircuitBreakerOptions();
            configure(topic.CircuitBreakerOptions);
        });

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
    /// Enable native dead letter queue support for this Kafka listener.
    /// Failed messages will be produced to the DLQ Kafka topic
    /// (default: "wolverine-dead-letter-queue") with exception details
    /// in Kafka headers.
    /// </summary>
    /// <returns></returns>
    public KafkaListenerConfiguration EnableNativeDeadLetterQueue()
    {
        add(topic => topic.NativeDeadLetterQueueEnabled = true);
        return this;
    }

    /// <summary>
    /// Disable native dead letter queue support for this Kafka listener.
    /// Failed messages will use Wolverine's default dead letter handling
    /// (database persistence).
    /// </summary>
    /// <returns></returns>
    public KafkaListenerConfiguration DisableNativeDeadLetterQueue()
    {
        add(topic => topic.NativeDeadLetterQueueEnabled = false);
        return this;
    }

    /// <summary>
    /// Disables stamping of the Kafka consumer group ID onto the GroupId property
    /// of each incoming envelope. Use this when the consumer group name is not
    /// meaningful as envelope metadata (e.g. when using PropagateGroupIdToPartitionKey).
    /// </summary>
    /// <returns></returns>
    public KafkaListenerConfiguration DisableConsumerGroupIdStamping()
    {
        add(topic => topic.StampConsumerGroupIdOnEnvelope = false);
        return this;
    }

    /// <summary>
    /// Marks this topic as owned by an external system. Wolverine
    /// will not attempt to create it during startup or delete it during resource
    /// teardown, even when AutoProvision is enabled on the parent transport.
    /// Use this when the calling identity lacks CreateTopics or DeleteTopics
    /// ACLs on the target topic.
    /// </summary>
    /// <returns></returns>
    public KafkaListenerConfiguration ExternallyOwned()
    {
        add(topic => topic.IsExternallyOwned = true);
        return this;
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
    /// Extends the Kafka consumer settings for this topic without replacing the
    /// existing topic-level configuration. If no topic-specific configuration exists,
    /// a new <see cref="ConsumerConfig"/> is created before applying the changes.
    /// </summary>
    /// <param name="configuration">An action that adds or updates consumer settings for this topic.</param>
    /// <returns>The current <see cref="KafkaListenerConfiguration"/> for fluent chaining.</returns>
    public KafkaListenerConfiguration ExtendConsumerConfiguration(Action<ConsumerConfig> configuration)
    {
        add(topic =>
        {
            topic.ConsumerConfig ??= new ConsumerConfig();
            configuration(topic.ConsumerConfig);
        });
        return this;
    }
}