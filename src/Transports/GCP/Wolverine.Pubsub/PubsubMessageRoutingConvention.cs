using Wolverine.Configuration;
using Wolverine.Transports;
using Wolverine.Util;

namespace Wolverine.Pubsub;

public class PubsubMessageRoutingConvention : MessageRoutingConvention<
    PubsubTransport,
    PubsubTopicListenerConfiguration,
    PubsubTopicSubscriberConfiguration,
    PubsubMessageRoutingConvention
>
{
    protected override (PubsubTopicListenerConfiguration, Endpoint) FindOrCreateListenerForIdentifier(string identifier,
        PubsubTransport transport, Type messageType)
    {
        var topic = transport.Topics[identifier];

        return (new PubsubTopicListenerConfiguration(topic), topic);
    }

    protected override (PubsubTopicSubscriberConfiguration, Endpoint) FindOrCreateSubscriber(string identifier,
        PubsubTransport transport)
    {
        var topic = transport.Topics[identifier];

        return (new PubsubTopicSubscriberConfiguration(topic), topic);
    }

    /// <summary>
    /// GH-4526. Under <see cref="MultipleHandlerBehavior.Separated"/> every handler of a message type past the
    /// first listens on its own subscription to the message type's topic, named from the handler type -- the same
    /// shape as a RabbitMQ queue per handler bound to the message type's exchange, or an Azure Service Bus
    /// subscription per handler on the message type's topic. The topic fans out, so the sender is untouched.
    /// </summary>
    protected override (PubsubTopicListenerConfiguration, Endpoint) FindOrCreateListenerForIdentifierUsingSeparatedHandler(string identifier,
        PubsubTransport transport, Type messageType, Type handlerType)
    {
        // Make sure the topic endpoint exists; it is the one the sender publishes to
        transport.Topics.FillDefault(identifier);

        var subscriptionName = SubscriptionNameForSeparatedHandler(transport, handlerType);
        var subscription = transport.SubscriptionFor(identifier, subscriptionName);

        return (new PubsubTopicListenerConfiguration(subscription), subscription);
    }

    internal static string SubscriptionNameForSeparatedHandler(PubsubTransport transport, Type handlerType)
    {
        // The same name NamingSource.FromHandlerType gives a handler's own topic, through the transport's
        // naming rules, so a handler type name that would be invalid as a subscription fails with the rule
        return transport.MaybeCorrectName(handlerType.ToMessageTypeName());
    }

    /// <summary>
    ///     Alternative syntax to specify the name for the queue that each message type will be sent
    /// </summary>
    /// <param name="namingRule"></param>
    /// <returns></returns>
    public PubsubMessageRoutingConvention TopicNameForSender(Func<Type, string?> namingRule)
    {
        return IdentifierForSender(namingRule);
    }
}