using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ;

/// <summary>
///     Conventional message routing for Rabbit MQ. By default, sends messages to an
///     exchange named after the MessageTypeName that is bound to a queue of the same name.
/// </summary>
public class RabbitMqMessageRoutingConvention : MessageRoutingConvention<RabbitMqTransport,
    RabbitMqListenerConfiguration, RabbitMqExchangeConfiguration, RabbitMqMessageRoutingConvention>
{
    protected override (RabbitMqListenerConfiguration, Endpoint) findOrCreateListenerForIdentifier(string identifier,
        RabbitMqTransport transport, Type messageType)
    {
        var queue = transport.Queues[identifier];
        return (new RabbitMqListenerConfiguration(queue), queue);
    }

    protected override (RabbitMqExchangeConfiguration, Endpoint) findOrCreateSubscriber(string identifier,
        RabbitMqTransport transport)
    {
        var exchange = transport.Exchanges[identifier];
        exchange.BindQueue(identifier, identifier);
        return (new RabbitMqExchangeConfiguration(exchange), exchange);
    }

    /// <summary>
    ///     Conventional name for the exchange .Same as IdentifierForSender
    /// </summary>
    /// <param name="nameForExchange"></param>
    /// <returns></returns>
    public RabbitMqMessageRoutingConvention ExchangeNameForSending(Func<Type, string?> nameForExchange)
    {
        return IdentifierForSender(nameForExchange);
    }
}