using Wolverine.AzureServiceBus.Internal;
using Wolverine.Transports;

namespace Wolverine.AzureServiceBus;

public class AzureServiceBusConfiguration : BrokerExpression<AzureServiceBusTransport, AzureServiceBusEndpoint, AzureServiceBusEndpoint, AzureServiceBusListenerConfiguration, AzureServiceBusSubscriberConfiguration, AzureServiceBusConfiguration>
{
    public AzureServiceBusConfiguration(AzureServiceBusTransport transport, WolverineOptions options) : base(transport, options)
    {

    }

    protected override AzureServiceBusListenerConfiguration createListenerExpression(AzureServiceBusEndpoint listenerEndpoint)
    {
        return new AzureServiceBusListenerConfiguration(listenerEndpoint);
    }

    protected override AzureServiceBusSubscriberConfiguration createSubscriberExpression(AzureServiceBusEndpoint subscriberEndpoint)
    {
        return new AzureServiceBusSubscriberConfiguration(subscriberEndpoint);
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