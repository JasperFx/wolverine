using Wolverine.AzureServiceBus.Internal;
using Wolverine.Transports;

namespace Wolverine.AzureServiceBus;

public class AzureServiceBusConfiguration : BrokerExpression<AzureServiceBusTransport, AzureServiceBusEndpoint, AzureServiceBusEndpoint, AzureServiceBusListenerConfiguration, AzureServiceBusSubscriberConfiguration, AzureServiceBusConfiguration>
{
    private readonly AzureServiceBusTransport _transport;
    private readonly WolverineOptions _options;

    public AzureServiceBusConfiguration(AzureServiceBusTransport transport, WolverineOptions options) : base(transport, options)
    {
        _transport = transport;
        _options = options;
    }

    protected override AzureServiceBusListenerConfiguration createListenerExpression(AzureServiceBusEndpoint listenerEndpoint)
    {
        throw new NotImplementedException();
    }

    protected override AzureServiceBusSubscriberConfiguration createSubscriberExpression(AzureServiceBusEndpoint subscriberEndpoint)
    {
        throw new NotImplementedException();
    }
}