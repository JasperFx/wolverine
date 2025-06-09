using System.Text.Json;
using Confluent.Kafka;
using Wolverine.Configuration;

namespace Wolverine.Kafka;

public class KafkaListenerConfiguration : ListenerConfiguration<KafkaListenerConfiguration, KafkaTopic>
{
    public KafkaListenerConfiguration(KafkaTopic endpoint) : base(endpoint)
    {
    }

    public KafkaListenerConfiguration(Func<KafkaTopic> source) : base(source)
    {
    }

    /// <summary>
    /// Use a custom interoperability strategy to map Wolverine messages to an upstream
    /// system's protocol
    /// </summary>
    /// <param name="mapper"></param>
    /// <returns></returns>
    public KafkaListenerConfiguration UseInterop(IKafkaEnvelopeMapper mapper)
    {
        add(e => e.Mapper = mapper);
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
        add(e =>
        {
            e.Mapper = new JsonOnlyMapper(e, options);
            e.MessageType = messageType;
        });

        return this;
    }
    
    /// <summary>
    /// Configure the consumer config for only this topic. This overrides the default
    /// settings at the transport level
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
}