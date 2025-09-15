using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.AzureServiceBus;

public class AzureServiceBusMessageRoutingConvention
    : MessageRoutingConvention<AzureServiceBusTransport, AzureServiceBusQueueListenerConfiguration,
        AzureServiceBusQueueSubscriberConfiguration, AzureServiceBusMessageRoutingConvention>
{
    protected override (AzureServiceBusQueueListenerConfiguration, Endpoint) FindOrCreateListenerForIdentifier(
        string identifier,
        AzureServiceBusTransport transport, Type messageType)
    {
        var queue = transport.Queues[identifier];
        return (new AzureServiceBusQueueListenerConfiguration(queue), queue);
    }

    protected override (AzureServiceBusQueueSubscriberConfiguration, Endpoint) FindOrCreateSubscriber(string identifier,
        AzureServiceBusTransport transport)
    {
        var queue = transport.Queues[identifier];
        return (new AzureServiceBusQueueSubscriberConfiguration(queue), queue);
    }

    /// <summary>
    ///     Specify naming rules for the subscribing queue for message types
    /// </summary>
    /// <param name="namingRule"></param>
    /// <returns></returns>
    public AzureServiceBusMessageRoutingConvention QueueNameForSender(Func<Type, string?> namingRule)
    {
        return IdentifierForSender(namingRule);
    }

    protected override (AzureServiceBusQueueListenerConfiguration, Endpoint) FindOrCreateListenerForIdentifierUsingSeparatedHandler(
        string identifier, AzureServiceBusTransport transport, Type messageType, Type handlerType)
    {
        throw new NotSupportedException(
            "The Azure Service Bus conventional routing by queues can not support conventional routing to multiple handlers in the same application. You will have to resort to explicit routing.");
    }
}