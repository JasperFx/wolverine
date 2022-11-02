using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Baseline;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AzureServiceBus;

public class AzureServiceBusTransport : BrokerTransport<AzureServiceBusEndpoint>, IAsyncDisposable
{
    public const string ProtocolName = "asb";
    
    public LightweightCache<string, AzureServiceBusQueue> Queues { get; }
    public LightweightCache<string, AzureServiceBusTopic> Topics { get; }

    public readonly List<AzureServiceBusSubscription> Subscriptions = new();
    private readonly Lazy<ServiceBusAdministrationClient> _managementClient;
    private readonly Lazy<ServiceBusClient> _busClient;

    public string? ConnectionString { get; set; }

    public AzureServiceBusTransport() : base(ProtocolName, "Azure Service Bus")
    {
        Queues = new(name => new AzureServiceBusQueue(this, name));
        Topics = new(name => new AzureServiceBusTopic(this, name));

        _managementClient = new Lazy<ServiceBusAdministrationClient>(() => new ServiceBusAdministrationClient(ConnectionString));
        _busClient = new Lazy<ServiceBusClient>(() => new ServiceBusClient(ConnectionString, ClientOptions));

        IdentifierDelimiter = ".";
    }

    protected override IEnumerable<AzureServiceBusEndpoint> endpoints()
    {
        foreach (var queue in Queues)
        {
            yield return queue;
        }

        foreach (var topic in Topics)
        {
            yield return topic;
        }

        foreach (var subscription in Subscriptions)
        {
            yield return subscription;
        }
    }

    protected override AzureServiceBusEndpoint findEndpointByUri(Uri uri)
    {
        switch (uri.Host)
        {
            case "queue":
                return Queues[uri.Segments[1]];
            
            case "topic":
                var topicName = uri.Segments[1].TrimEnd('/');
                if (uri.Segments.Length == 3)
                {
                    var subscription = Subscriptions.FirstOrDefault(x => x.Uri == uri);
                    if (subscription != null) return subscription;
                    
                    var subscriptionName = uri.Segments.Last().TrimEnd('/');
                    var topic = Topics[topicName];
                    subscription = new AzureServiceBusSubscription(this, topic, subscriptionName);
                    Subscriptions.Add(subscription);

                    return subscription;
                }
                else
                {
                    return Topics[topicName];
                }

        }

        throw new ArgumentOutOfRangeException(nameof(uri));
    }
    
    public ServiceBusClientOptions ClientOptions { get; } = new()
    {
        TransportType = ServiceBusTransportType.AmqpTcp
    };

    public ServiceBusAdministrationClient ManagementClient => _managementClient.Value;

    public ServiceBusClient BusClient => _busClient.Value;

    public override ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
        // we're going to use a client per endpoint
        return ValueTask.CompletedTask;
    }

    public override IEnumerable<PropertyColumn> DiagnosticColumns()
    {
        yield return new PropertyColumn("Queue", "Name");
        yield return new PropertyColumn(nameof(QueueProperties.Status));
    }

    public async ValueTask DisposeAsync()
    {
        if (_busClient.IsValueCreated)
        {
            await _busClient.Value.DisposeAsync();
        }
    }
}