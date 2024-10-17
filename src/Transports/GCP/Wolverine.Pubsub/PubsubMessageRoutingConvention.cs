using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.Pubsub;

public class PubsubMessageRoutingConvention : MessageRoutingConvention<
    PubsubTransport,
    PubsubSubscriptionConfiguration,
    PubsubTopicConfiguration,
    PubsubMessageRoutingConvention
> {
    protected override (PubsubSubscriptionConfiguration, Endpoint) FindOrCreateListenerForIdentifier(
        string identifier,
        PubsubTransport transport,
        Type messageType
    ) {
        var topic = transport.Topics[identifier];
        var subscription = topic.FindOrCreateSubscription();

        return (new PubsubSubscriptionConfiguration(subscription), subscription);
    }

    protected override (PubsubTopicConfiguration, Endpoint) FindOrCreateSubscriber(
        string identifier,
        PubsubTransport transport
    ) {
        var topic = transport.Topics[identifier];

        return (new PubsubTopicConfiguration(topic), topic);
    }

    /// <summary>
    ///     Specify naming rules for the subscribing topic for message types
    /// </summary>
    /// <param name="namingRule"></param>
    /// <returns></returns>
    public PubsubMessageRoutingConvention TopicNameForSender(Func<Type, string?> namingRule) => IdentifierForSender(namingRule);
}
