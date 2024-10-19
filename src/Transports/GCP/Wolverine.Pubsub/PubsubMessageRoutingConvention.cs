using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.Pubsub;

public class PubsubMessageRoutingConvention : MessageRoutingConvention<
    PubsubTransport,
    PubsubTopicListenerConfiguration,
    PubsubTopicSubscriberConfiguration,
    PubsubMessageRoutingConvention
> {
    protected override (PubsubTopicListenerConfiguration, Endpoint) FindOrCreateListenerForIdentifier(string identifier, PubsubTransport transport, Type messageType) {
        var topic = transport.Topics[identifier];

        return (new PubsubTopicListenerConfiguration(topic), topic);
    }

    protected override (PubsubTopicSubscriberConfiguration, Endpoint) FindOrCreateSubscriber(string identifier, PubsubTransport transport) {
        var topic = transport.Topics[identifier];

        return (new PubsubTopicSubscriberConfiguration(topic), topic);
    }

    /// <summary>
    ///     Alternative syntax to specify the name for the queue that each message type will be sent
    /// </summary>
    /// <param name="namingRule"></param>
    /// <returns></returns>
    public PubsubMessageRoutingConvention TopicNameForSender(Func<Type, string?> namingRule) => IdentifierForSender(namingRule);
}
