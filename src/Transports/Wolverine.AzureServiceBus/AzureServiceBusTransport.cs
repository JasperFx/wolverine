using Azure.Messaging.ServiceBus;
using Baseline;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AzureServiceBus;

public static class AzureServiceBusTransportExtensions
{
    /// <summary>
    ///     Quick access to the Rabbit MQ Transport within this application.
    ///     This is for advanced usage
    /// </summary>
    /// <param name="endpoints"></param>
    /// <returns></returns>
    internal static AzureServiceBusTransport AzureServiceBusTransport(this WolverineOptions endpoints)
    {
        var transports = endpoints.As<WolverineOptions>().Transports;

        return transports.GetOrCreate<AzureServiceBusTransport>();
    }

    public static IAzureServiceBusConfiguration UseAzureServiceBus(this WolverineOptions endpoints,
        string connectionString, Action<ServiceBusClientOptions>? configure = null)
    {
        var transport = endpoints.AzureServiceBusTransport();
        transport.ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString);
        ServiceBusClientOptions options = new ServiceBusClientOptions();
        configure?.Invoke(transport.ClientOptions);

        return new AzureServiceBusConfiguration(transport, endpoints);
    }
}

public interface IAzureServiceBusConfiguration
{
    
}

internal class AzureServiceBusConfiguration : IAzureServiceBusConfiguration
{
    private readonly AzureServiceBusTransport _transport;
    private readonly WolverineOptions _options;

    public AzureServiceBusConfiguration(AzureServiceBusTransport transport, WolverineOptions options)
    {
        _transport = transport;
        _options = options;
    }


}

internal class AzureServiceBusTransport : TransportBase<AzureServiceBusEndpoint>
{
    public const string ProtocolName = "asb";
    
    public LightweightCache<string, AzureServiceBusQueue> Queues { get; }
    public string ConnectionString { get; set; }

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
}

internal abstract class AzureServiceBusEndpoint : Endpoint
{
    public AzureServiceBusTransport Parent { get; }

    public AzureServiceBusEndpoint(AzureServiceBusTransport parent, Uri uri, EndpointRole role) : base(uri, role)
    {
        Parent = parent;
    }
}

internal class AzureServiceBusQueue : AzureServiceBusEndpoint
{
    public AzureServiceBusQueue(AzureServiceBusTransport parent, string queueName, EndpointRole role) : base(parent, new Uri($"{AzureServiceBusTransport.ProtocolName}://queue/{queueName}"), role)
    {
    }

    public override IListener BuildListener(IWolverineRuntime runtime, IReceiver receiver)
    {
        throw new NotImplementedException();
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        throw new NotImplementedException();
    }
}