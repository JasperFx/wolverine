using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Transports;
using Wolverine.Util;

namespace Wolverine.RabbitMQ;

/// <summary>
///     Conventional message routing for Rabbit MQ. By default, sends messages to an
///     exchange named after the MessageTypeName that is bound to a queue of the same name.
/// </summary>
public class RabbitMqMessageRoutingConvention : MessageRoutingConvention<RabbitMqTransport,
    RabbitMqConventionalListenerConfiguration, RabbitMqConventionalExchangeConfiguration, RabbitMqMessageRoutingConvention>
{
    private readonly WolverineOptions _wolverineOptions;

    internal RabbitMqMessageRoutingConvention(WolverineOptions wolverineOptions)
    {
        _wolverineOptions = wolverineOptions;
    }

    protected override (RabbitMqConventionalListenerConfiguration, Endpoint) FindOrCreateListenerForIdentifier(string identifier,
        RabbitMqTransport transport, Type messageType)
    {
        var queue = transport.Queues[identifier];
        return (new RabbitMqConventionalListenerConfiguration(queue, transport, _identifierForSender), queue);
    }

    protected override void ApplyListenerRoutingDefaults(string listenerIdentifier, RabbitMqTransport transport, Type messageType)
    {
        var queue = transport.Queues[listenerIdentifier];
        // If there's no custom bindings, bind to an exchange with the default convention
        if (!queue.HasBindings)
        {
            var identifier = _identifierForSender(messageType);
            if (identifier is null)
                return;
            var name = transport.MaybeCorrectName(identifier);
            var exchange = transport.Exchanges[name];
            queue.BindExchange(exchange.Name, exchange.Name);
        }
    }

    protected override (RabbitMqConventionalExchangeConfiguration, Endpoint) FindOrCreateSubscriber(string identifier,
        RabbitMqTransport transport)
    {
        var exchange = transport.Exchanges[identifier];
        return (new RabbitMqConventionalExchangeConfiguration(exchange, _wolverineOptions), exchange);
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