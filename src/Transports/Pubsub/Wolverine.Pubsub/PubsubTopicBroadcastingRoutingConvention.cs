using Wolverine.Pubsub.Internal;
using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.Pubsub;

public class PubsubTopicBroadcastingRoutingConvention : MessageRoutingConvention<
    PubsubTransport,
    PubsubSubscriptionConfiguration,
    PubsubTopicConfiguration,
    PubsubTopicBroadcastingRoutingConvention
> {
    private Func<Type, string>? _subscriptionNameSource;

    protected override (PubsubSubscriptionConfiguration, Endpoint) FindOrCreateListenerForIdentifier(
        string identifier,
        PubsubTransport transport,
        Type messageType
    ) {
        var topic = transport.Topics[identifier];
        var subscriptionName = _subscriptionNameSource == null ? identifier : _subscriptionNameSource(messageType);
        var subscription = topic.FindOrCreateSubscription(subscriptionName);

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
    /// Override the naming convention for topics. Identical in functionality to IdentifierForSender()
    /// </summary>
    /// <param name="nameSource"></param>
    /// <returns></returns>
    public PubsubTopicBroadcastingRoutingConvention TopicNameForSender(Func<Type, string> nameSource) => IdentifierForSender(nameSource);

    /// <summary>
    /// Override the subscription name for a message type. By default this would be the same as the topic
    /// </summary>
    /// <param name="nameSource"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public PubsubTopicBroadcastingRoutingConvention SubscriptionNameForListener(Func<Type, string> nameSource) {
        _subscriptionNameSource = nameSource;

        return this;
    }

    /// <summary>
    /// Override the topic name by message type for listeners. This has the same functionality as IdentifierForListener()
    /// </summary>
    /// <param name="nameSource"></param>
    /// <returns></returns>
    public PubsubTopicBroadcastingRoutingConvention TopicNameForListener(Func<Type, string> nameSource) => IdentifierForListener(nameSource);
}
