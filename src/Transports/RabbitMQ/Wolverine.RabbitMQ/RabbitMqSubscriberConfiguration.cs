using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime.Interop.MassTransit;
using Wolverine.Runtime.Routing;

namespace Wolverine.RabbitMQ;

public class
    RabbitMqSubscriberConfiguration : SubscriberConfiguration<RabbitMqSubscriberConfiguration, RabbitMqEndpoint>
{
    internal RabbitMqSubscriberConfiguration(RabbitMqEndpoint endpoint) : base(endpoint)
    {
    }

    /// <summary>
    ///     Configure this Rabbit MQ endpoint for interoperability with MassTransit
    /// </summary>
    /// <param name="configure">Optionally configure the JSON serialization for MassTransit</param>
    /// <returns></returns>
    public RabbitMqSubscriberConfiguration UseMassTransitInterop(Action<IMassTransitInterop>? configure = null)
    {
        add(e => e.UseMassTransitInterop(configure));
        return this;
    }

    /// <summary>
    ///     Configure this Rabbit MQ endpoint for interoperability with NServiceBus
    /// </summary>
    /// <returns></returns>
    public RabbitMqSubscriberConfiguration UseNServiceBusInterop()
    {
        add(e => e.UseNServiceBusInterop());
        return this;
    }

    /// <summary>
    /// Use a custom interoperability strategy to map Wolverine messages to an upstream
    /// system's protocol
    /// </summary>
    /// <param name="mapper"></param>
    /// <returns></returns>
    public RabbitMqSubscriberConfiguration UseInterop(IRabbitMqEnvelopeMapper mapper)
    {
        add(e => e.EnvelopeMapper = mapper);
        return this;
    }
}


public sealed class RabbitMqConventionalExchangeConfiguration : RabbitMqExchangeConfiguration
{
    private readonly WolverineOptions _options;

    internal RabbitMqConventionalExchangeConfiguration(RabbitMqExchange endpoint, WolverineOptions options) : base(endpoint)
    {
        _options = options;
    }
    
    
    /// <summary>
    /// Publish messages that are of type T or could be cast to type T to a Rabbit MQ
    /// topic exchange using the supplied function to determine the topic for the message
    /// </summary>
    /// <param name="topicSource"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public RabbitMqExchangeConfiguration PublishMessagesToTopic<T>(Func<T, string> topicSource)
    {
        add(e =>
        {
            e.ExchangeType = RabbitMQ.ExchangeType.Topic;
            e.RoutingType = RoutingMode.ByTopic;
            var routing = new TopicRouting<T>(topicSource, e);
            _options.PublishWithMessageRoutingSource(routing);
        });

        return this;
    }
    
}

public class RabbitMqExchangeConfiguration : SubscriberConfiguration<RabbitMqExchangeConfiguration, RabbitMqExchange>
{
    internal RabbitMqExchangeConfiguration(RabbitMqExchange endpoint) : base(endpoint)
    {
    }

    /// <summary>
    ///     Configure this Rabbit MQ endpoint for interoperability with MassTransit
    /// </summary>
    /// <param name="configure">Optionally configure the JSON serialization for MassTransit</param>
    /// <returns></returns>
    public RabbitMqExchangeConfiguration UseMassTransitInterop(Action<IMassTransitInterop>? configure = null)
    {
        add(e => e.UseMassTransitInterop(configure));
        return this;
    }

    /// <summary>
    ///     Configure this Rabbit MQ endpoint for interoperability with NServiceBus
    /// </summary>
    /// <returns></returns>
    public RabbitMqExchangeConfiguration UseNServiceBusInterop()
    {
        add(e => e.UseNServiceBusInterop());
        return this;
    }

    /// <summary>
    ///     Modify the exchange type, the default is fan out
    /// </summary>
    /// <param name="exchangeType"></param>
    /// <returns></returns>
    public RabbitMqExchangeConfiguration ExchangeType(ExchangeType exchangeType)
    {
        add(e => e.ExchangeType = exchangeType);
        return this;
    }
}