using Confluent.Kafka;
using Wolverine.Kafka.Internals;
using Wolverine.Transports;

namespace Wolverine.Kafka;

public class KafkaTransportExpression : BrokerExpression<KafkaTransport, KafkaTopic, KafkaTopic, KafkaListenerConfiguration, KafkaSubscriberConfiguration, KafkaTransportExpression>
{
    private readonly KafkaTransport _transport;

    internal KafkaTransportExpression(KafkaTransport transport, WolverineOptions options) : base(transport, options)
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
    /// Configure the Kafka message producers within the Wolverine transport. This can be
    /// overridden at the topic level.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public KafkaTransportExpression ConfigureProducers(Action<ProducerConfig> configure)
    {
        configure(_transport.ProducerConfig);
        return this;
    }

    /// <summary>
    /// Configure the Kafka message producer builders within the Wolverine transport
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public KafkaTransportExpression ConfigureProducerBuilders(Action<ProducerBuilder<string, byte[]>> configure)
    {
        _transport.ConfigureProducerBuilders = configure;
        return this;
    }

    /// <summary>
    /// Configure the Kafka message consumers within the Wolverine transport. This can be
    /// overridden at the topic level.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public KafkaTransportExpression ConfigureConsumers(Action<ConsumerConfig> configure)
    {
        configure(_transport.ConsumerConfig);
        return this;
    }

    /// <summary>
    /// Configure the Kafka consumer builders within the Wolverine transport
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public KafkaTransportExpression ConfigureConsumerBuilders(Action<ConsumerBuilder<string, byte[]>> configure)
    {
        _transport.ConfigureConsumerBuilders = configure;
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
    /// Configure the Kafka admin client builders within the Wolverine transport
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public KafkaTransportExpression ConfigureAdminClientBuilders(Action<AdminClientBuilder> configure)
    {
        _transport.ConfigureAdminClientBuilders = configure;
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

    protected override KafkaListenerConfiguration createListenerExpression(KafkaTopic listenerEndpoint)
    {
        return new KafkaListenerConfiguration(listenerEndpoint);
    }

    protected override KafkaSubscriberConfiguration createSubscriberExpression(KafkaTopic subscriberEndpoint)
    {
        return new KafkaSubscriberConfiguration(subscriberEndpoint);
    }
}
