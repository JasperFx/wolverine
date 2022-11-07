using Wolverine.AzureServiceBus.Internal;
using Wolverine.Transports;

namespace Wolverine.AzureServiceBus;

public class AzureServiceBusConfiguration : BrokerExpression<AzureServiceBusTransport, AzureServiceBusQueue, AzureServiceBusQueue, AzureServiceBusQueueListenerConfiguration, AzureServiceBusQueueSubscriberConfiguration, AzureServiceBusConfiguration>
{
    public AzureServiceBusConfiguration(AzureServiceBusTransport transport, WolverineOptions options) : base(transport, options)
    {

    }

    protected override AzureServiceBusQueueListenerConfiguration createListenerExpression(AzureServiceBusQueue listenerEndpoint)
    {
        return new AzureServiceBusQueueListenerConfiguration(listenerEndpoint);
    }

    protected override AzureServiceBusQueueSubscriberConfiguration createSubscriberExpression(AzureServiceBusQueue subscriberEndpoint)
    {
        return new AzureServiceBusQueueSubscriberConfiguration(subscriberEndpoint);
    }

    public AzureServiceBusConfiguration UseConventionalRouting(
        Action<AzureServiceBusMessageRoutingConvention>? configure = null)
    {
        var routing = new AzureServiceBusMessageRoutingConvention();
        configure?.Invoke(routing);
        
        Options.RouteWith(routing);
        
        return this;
    }
}