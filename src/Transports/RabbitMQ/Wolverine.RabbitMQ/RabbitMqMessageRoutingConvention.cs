using JasperFx.Core.Reflection;
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
    RabbitMqConventionalListenerConfiguration, RabbitMqExchangeConfiguration, RabbitMqMessageRoutingConvention>
{
    protected override (RabbitMqConventionalListenerConfiguration, Endpoint) FindOrCreateListenerForIdentifier(string identifier,
        RabbitMqTransport transport, Type messageType)
    {
        var queue = transport.Queues[identifier];
        return (new RabbitMqConventionalListenerConfiguration(queue, transport, _identifierForSender), queue);
    }

    protected override void ApplyListenerRoutingDefaults(string listenerIdentifier, RabbitMqTransport transport, Type messageType)
    {
        var queue = transport.Queues[listenerIdentifier];

        var identifier = _identifierForSender(messageType);
        if (identifier is null)
            return;

        var name = transport.MaybeCorrectName(identifier);
        var exchange = transport.Exchanges[name];

        // Per-exchange dedup. The original guard short-circuited on
        // `queue.HasBindings` so user-configured custom bindings on this queue
        // wouldn't get a default binding stacked on top — but under
        // NamingSource.FromHandlerType a single handler queue legitimately needs
        // a binding to every message-type exchange the handler accepts, and that
        // pattern dispatches ApplyListenerRoutingDefaults once per (handlerType,
        // messageType) pair. The earlier guard silently dropped every binding
        // after the first. Narrow the suppression to "we already bind THIS queue
        // to THIS exchange" so the multi-message-type case binds correctly while
        // still leaving custom user bindings (and prior passes for the same
        // message type) untouched. See GH-2681.
        if (queue.Bindings().Any(b => b.ExchangeName == exchange.Name))
        {
            return;
        }

        queue.BindExchange(exchange.Name, exchange.Name);
    }

    protected override (RabbitMqExchangeConfiguration, Endpoint) FindOrCreateSubscriber(string identifier,
        RabbitMqTransport transport)
    {
        var exchange = transport.Exchanges[identifier];
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

    protected override (RabbitMqConventionalListenerConfiguration, Endpoint) FindOrCreateListenerForIdentifierUsingSeparatedHandler(
        string identifier, RabbitMqTransport transport, Type messageType, Type handlerType)
    {
        var exchange = transport.Exchanges[identifier];
        var queueName = transport.MaybeCorrectName(handlerType.FullNameInCode());
        var queue = transport.Queues[queueName];

        queue.BindExchange(exchange.Name, $"{exchange.Name}-{queue.QueueName}");
        return (new RabbitMqConventionalListenerConfiguration(queue, transport, _identifierForSender), queue);
    }
}