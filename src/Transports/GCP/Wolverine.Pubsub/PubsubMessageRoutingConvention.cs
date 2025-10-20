using Wolverine.Configuration;
using Wolverine.Transports;

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
        var topicName = _identifierForSender(messageType);
        var topic = transport.Topics[topicName];
        var subscription = topic.GcpSubscriptions[identifier];

        return (new PubsubTopicListenerConfiguration(subscription), subscription);
    }

    protected override (PubsubTopicSubscriberConfiguration, Endpoint) FindOrCreateSubscriber(string identifier,
        PubsubTransport transport)
    {
        var topic = transport.Topics[identifier];

        return (new PubsubTopicSubscriberConfiguration(topic), topic);
    }

    protected override (PubsubTopicListenerConfiguration, Endpoint) FindOrCreateListenerForIdentifierUsingSeparatedHandler(string identifier,
        PubsubTransport transport, Type messageType, Type handlerType)
    {
        throw new NotSupportedException(
            "The Google Pubsub transport does not (yet) support conventional routing to multiple handlers in the same application. You will have to resort to explicit routing.");
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