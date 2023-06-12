using System;
using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime.Interop.MassTransit;

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