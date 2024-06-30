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

        var subscriptionName = _subscriptionNameSource == null ? identifier : _subscriptionNameSource(messageType);

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