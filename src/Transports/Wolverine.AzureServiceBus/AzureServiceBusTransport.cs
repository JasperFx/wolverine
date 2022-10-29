using Azure.Messaging.ServiceBus;
using Baseline;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AzureServiceBus;

public class AzureServiceBusTransport : BrokerTransport<AzureServiceBusEndpoint>
{
    public const string ProtocolName = "asb";
    
    public LightweightCache<string, AzureServiceBusQueue> Queues { get; }
    public string? ConnectionString { get; set; }

    public AzureServiceBusTransport() : base(ProtocolName, "Azure Service Bus")
    {
        Queues = new(name => new AzureServiceBusQueue(this, name, EndpointRole.Application));
    }

    protected override IEnumerable<AzureServiceBusEndpoint> endpoints()
    {
        throw new NotImplementedException();
    }

    protected override AzureServiceBusEndpoint findEndpointByUri(Uri uri)
    {
        throw new NotImplementedException();
    }
    
    public ServiceBusClientOptions ClientOptions { get; } = new()
    {
        TransportType = ServiceBusTransportType.AmqpTcp
    };

    public override ValueTask ConnectAsync(IWolverineRuntime logger)
    {
        throw new NotImplementedException();
    }

    public override IEnumerable<PropertyColumn> DiagnosticColumns()
    {
        throw new NotImplementedException();
    }
}