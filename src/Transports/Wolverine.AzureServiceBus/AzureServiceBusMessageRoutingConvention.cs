using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.AzureServiceBus;

public class AzureServiceBusMessageRoutingConvention 
    : MessageRoutingConvention<AzureServiceBusTransport, AzureServiceBusListenerConfiguration, AzureServiceBusSubscriberConfiguration, AzureServiceBusMessageRoutingConvention>
{
    protected override (AzureServiceBusListenerConfiguration, Endpoint) findOrCreateListenerForIdentifier(string identifier,
        AzureServiceBusTransport transport)
    {
        var queue = transport.Queues[identifier];
        return (new AzureServiceBusListenerConfiguration(queue), queue);
    }

    protected override (AzureServiceBusSubscriberConfiguration, Endpoint) findOrCreateSubscriber(string identifier,
        AzureServiceBusTransport transport)
    {
        var queue = transport.Queues[identifier];
        return (new AzureServiceBusSubscriberConfiguration(queue), queue);
    }

    /// <summary>
    /// Specify naming rules for the subscribing queue for message types
    /// </summary>
    /// <param name="namingRule"></param>
    /// <returns></returns>
    public AzureServiceBusMessageRoutingConvention  QueueNameForSender(Func<Type, string?> namingRule)
    {
        return IdentifierForSender(namingRule);
    }
}