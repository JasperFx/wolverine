using System.Text.Json;
using Confluent.Kafka;
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
}