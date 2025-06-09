using System.Text.Json;
using Confluent.Kafka;
using Wolverine.Configuration;

namespace Wolverine.Kafka;

public class KafkaSubscriberConfiguration : SubscriberConfiguration<KafkaSubscriberConfiguration, KafkaTopic>
{
    internal KafkaSubscriberConfiguration(KafkaTopic endpoint) : base(endpoint)
    {
    }

    /// <summary>
    /// Use a custom interoperability strategy to map Wolverine messages to an upstream
    /// system's protocol
    /// </summary>
    /// <param name="mapper"></param>
    /// <returns></returns>
    public KafkaSubscriberConfiguration UseInterop(IKafkaEnvelopeMapper mapper)
    {
        add(e => e.Mapper = mapper);
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
        add(e => e.Mapper = new JsonOnlyMapper(e, options ?? new JsonSerializerOptions()));
        return this;
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
}