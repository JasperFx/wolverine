using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.AzureServiceBus;

public class AzureServiceBusTopicBroadcastingRoutingConvention : MessageRoutingConvention<AzureServiceBusTransport,
    AzureServiceBusSubscriptionListenerConfiguration,
    AzureServiceBusTopicSubscriberConfiguration, AzureServiceBusTopicBroadcastingRoutingConvention>
{
    private Func<Type,string>? _subscriptionNameSource;

    protected override (AzureServiceBusSubscriptionListenerConfiguration, Endpoint) FindOrCreateListenerForIdentifier(
        string identifier,
        AzureServiceBusTransport transport, Type messageType)
    {
        var topic = transport.Topics[identifier];

        // `identifier` arrived already sanitized -- MessageRoutingConvention runs every listener
        // identifier through transport.MaybeCorrectName. A caller-supplied _subscriptionNameSource
        // does not go through that path, so it has to be sanitized here or an illegal character
        // reaches the management API as a 400 that reads only as a broker-init timeout. GH-3786:
        // SubscriptionNameForListener(t => t.Name.ToLowerInvariant()) over a Handle(BatchedItem[])
        // handler produced the subscription name "batcheditem[]" against a correctly-named topic.
        var subscriptionName = _subscriptionNameSource == null
            ? identifier
            : transport.SanitizeIdentifier(_subscriptionNameSource(messageType));

        var subscription =
            transport.Subscriptions.FirstOrDefault(x =>
                x.Topic.TopicName == identifier && x.SubscriptionName == subscriptionName);

        if (subscription == null)
        {
            subscription = new AzureServiceBusSubscription(transport, topic, subscriptionName);
            transport.Subscriptions.Add(subscription);
        }

        return (new AzureServiceBusSubscriptionListenerConfiguration(subscription), subscription);
    }

    protected override (AzureServiceBusSubscriptionListenerConfiguration, Endpoint) FindOrCreateListenerForIdentifierUsingSeparatedHandler(
        string topicName, AzureServiceBusTransport transport, Type messageType, Type handlerType)
    {
        var topic = transport.Topics[topicName];
        
        // Same reasoning as FindOrCreateListenerForIdentifier above: MaybeCorrectName already
        // sanitizes the default, the caller-supplied source does not sanitize itself.
        var subscriptionName = _subscriptionNameSource == null
            ? transport.MaybeCorrectName(handlerType.FullName!)
            : transport.SanitizeIdentifier(_subscriptionNameSource(handlerType));

        var subscription =
            transport.Subscriptions.FirstOrDefault(x =>
                x.Topic.TopicName == topicName && x.SubscriptionName == subscriptionName);

        if (subscription == null)
        {
            subscription = new AzureServiceBusSubscription(transport, topic, subscriptionName);
            transport.Subscriptions.Add(subscription);
        }

        return (new AzureServiceBusSubscriptionListenerConfiguration(subscription), subscription);
    }

    protected override (AzureServiceBusTopicSubscriberConfiguration, Endpoint) FindOrCreateSubscriber(string identifier,
        AzureServiceBusTransport transport)
    {
        var topic = transport.Topics[identifier];
        return (new AzureServiceBusTopicSubscriberConfiguration(topic), topic);
    }

    /// <summary>
    /// Override the naming convention for topics. Identical in functionality to IdentifierForSender()
    /// </summary>
    /// <param name="nameSource"></param>
    /// <returns></returns>
    public AzureServiceBusTopicBroadcastingRoutingConvention TopicNameForSender(Func<Type, string> nameSource)
    {
        return IdentifierForSender(nameSource);
    }

    /// <summary>
    /// Override the subscription name for a message type. By default this would be the same as the topic
    /// </summary>
    /// <param name="nameSource"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public AzureServiceBusTopicBroadcastingRoutingConvention SubscriptionNameForListener(Func<Type, string> nameSource)
    {
        _subscriptionNameSource = nameSource;
        return this;
    }

    /// <summary>
    /// Override the topic name by message type for listeners. This has the same functionality as IdentifierForListener()
    /// </summary>
    /// <param name="nameSource"></param>
    /// <returns></returns>
    public AzureServiceBusTopicBroadcastingRoutingConvention TopicNameForListener(Func<Type, string> nameSource)
    {
        return IdentifierForListener(nameSource);
    }
}