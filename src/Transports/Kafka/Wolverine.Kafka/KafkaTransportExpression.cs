using Confluent.Kafka;
using Wolverine.Kafka.Internals;

namespace Wolverine.Kafka;

public class KafkaTransportExpression
{
    private readonly KafkaTransport _transport;

    internal KafkaTransportExpression(KafkaTransport transport)
    {
        _transport = transport;
    }

    /// <summary>
    /// Configure both the producer and consumer config of the underlying Kafka client
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public KafkaTransportExpression ConfigureClient(Action<ClientConfig> configure)
    {
        configure(_transport.ConsumerConfig);
        configure(_transport.ProducerConfig);
        configure(_transport.AdminClientConfig);

        return this;
    }

    /// <summary>
    /// Configure the Kafka message producers within the Wolverine transport
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public KafkaTransportExpression ConfigureProducers(Action<ProducerConfig> configure)
    {
        configure(_transport.ProducerConfig);
        return this;
    }
    
    /// <summary>
    /// Configure the Kafka message consumers within the Wolverine transport
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public KafkaTransportExpression ConfigureConsumers(Action<ConsumerConfig> configure)
    {
        configure(_transport.ConsumerConfig);
        return this;
    }

    /// <summary>
    /// Create newly used Kafka topics on endpoint activation if the topic is missing
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public KafkaTransportExpression AutoProvision(Action<AdminClientConfig>? configure = null)
    {
        _transport.AutoProvision = true;
        configure?.Invoke(_transport.AdminClientConfig);
        return this;
    }

    /// <summary>
    /// Deletes and rebuilds topics on application startup
    /// </summary>
    /// <returns></returns>
    public KafkaTransportExpression DeleteExistingTopicsOnStartup()
    {
        _transport.AutoPurgeAllQueues = true;
        return this;
    }
}