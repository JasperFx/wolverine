using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Wolverine.Configuration;
using Wolverine.Kafka.Internals;

namespace Wolverine.Kafka;

public class KafkaSubscriberConfiguration : InteroperableSubscriberConfiguration<KafkaSubscriberConfiguration, KafkaTopic, IKafkaEnvelopeMapper, KafkaEnvelopeMapper>
{
    internal KafkaSubscriberConfiguration(KafkaTopic endpoint) : base(endpoint)
    {
    }
    
    /// <summary>
    /// If you need to do anything "special" to create topics at runtime with Wolverine,
    /// this overrides the simple logic that Wolverine uses and replaces
    /// it with whatever you need to do having full access to the Kafka IAdminClient
    /// and the Wolverine KafkaTopic configuration
    /// </summary>
    /// <param name="creation"></param>
    /// <returns></returns>
    public KafkaSubscriberConfiguration TopicCreation(Func<IAdminClient, KafkaTopic, Task> creation)
    {
        if (creation == null)
        {
            throw new ArgumentNullException(nameof(creation));
        }

        add(topic => topic.CreateTopicFunc = creation);
        return this;
    }

    /// <summary>
    /// Fine tune the TopicSpecification for this Kafka Topic if it is being created by Wolverine
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public KafkaSubscriberConfiguration Specification(Action<TopicSpecification> configure)
    {
        if (configure == null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        add(topic => configure(topic.Specification));
        return this;
    }

    /// <summary>
    /// Publish only the raw, serialized JSON representation of messages to the downstream
    /// Kafka subscribers
    /// </summary>
    /// <param name="options"></param>
    /// <returns></returns>
    public KafkaSubscriberConfiguration PublishRawJson(JsonSerializerOptions? options = null)
    {
        return UseInterop((e, _) => new JsonOnlyMapper(e, options ?? new JsonSerializerOptions()));
    }

    /// <summary>
    /// Configure the producer config for only this topic. This overrides the default
    /// settings at the transport level
    /// </summary>
    /// <param name="configuration"></param>
    /// <returns></returns>
    public KafkaSubscriberConfiguration ConfigureProducer(Action<ProducerConfig> configuration)
    {
        add(topic =>
        {
            var config = new ProducerConfig();
            configuration(config);

            topic.ProducerConfig = config;
        });
        return this;
    }

    /// <summary>
    /// Opt this topic's producer into the idempotent producer (<c>enable.idempotence = true</c>, which
    /// implies <c>acks=all</c> and bounded in-flight requests) so producer-side retries can't write
    /// duplicates to the broker. Opt-in; producer→broker de-duplication only (not transactional
    /// exactly-once). See GH-3149. Call after <see cref="ConfigureProducer"/> if you also use that.
    /// </summary>
    public KafkaSubscriberConfiguration UseIdempotentProducer()
    {
        add(topic =>
        {
            topic.ProducerConfig ??= new ProducerConfig();
            topic.ProducerConfig.EnableIdempotence = true;
        });
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
    public KafkaSubscriberConfiguration ExternallyOwned()
    {
        add(topic => topic.IsExternallyOwned = true);
        return this;
    }
}