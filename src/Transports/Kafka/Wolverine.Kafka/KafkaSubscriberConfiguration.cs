using System.Text.Json;
using Confluent.Kafka;
using Wolverine.Configuration;
using Wolverine.Kafka.Internals;

namespace Wolverine.Kafka;

public class KafkaSubscriberConfiguration : InteroperableSubscriberConfiguration<KafkaSubscriberConfiguration, KafkaTopic, IKafkaEnvelopeMapper, KafkaEnvelopeMapper>
{
    internal KafkaSubscriberConfiguration(KafkaTopic endpoint) : base(endpoint)
    {
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
}