using Azure;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using JasperFx.Core;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AzureServiceBus;

public class AzureServiceBusTransport : BrokerTransport<AzureServiceBusEndpoint>, IAsyncDisposable
{
    public const string ProtocolName = "asb";
    public const string ResponseEndpointName = "AzureServiceBusResponses";
    public const string RetryEndpointName = "AzureServiceBusRetries";
    private readonly Lazy<ServiceBusClient> _busClient;
    private readonly Lazy<ServiceBusAdministrationClient> _managementClient;

    public readonly List<AzureServiceBusSubscription> Subscriptions = new();
    public const string DeadLetterQueueName = "wolverine-dead-letter-queue";

    public AzureServiceBusTransport() : base(ProtocolName, "Azure Service Bus")
    {
        Queues = new(name => new AzureServiceBusQueue(this, name));
        Topics = new(name => new AzureServiceBusTopic(this, name));

        _managementClient =
            new Lazy<ServiceBusAdministrationClient>(createServiceBusAdministrationClient);
        _busClient = new Lazy<ServiceBusClient>(createServiceBusClient);

        IdentifierDelimiter = ".";
    }

    /// <summary>
    /// Is this transport connection allowed to build and use response and retry queues
    /// for just this node?
    /// </summary>
    public bool SystemQueuesEnabled { get; set; } = true;

    public LightweightCache<string, AzureServiceBusQueue> Queues { get; }
    public LightweightCache<string, AzureServiceBusTopic> Topics { get; }

    public string? ConnectionString { get; set; }

    public string? FullyQualifiedNamespace { get; set; }
    public TokenCredential? TokenCredential { get; set; }
    public AzureNamedKeyCredential? NamedKeyCredential { get; set; }
    public AzureSasCredential? SasCredential { get; set; }

    public ServiceBusClientOptions ClientOptions { get; } = new()
    {
        TransportType = ServiceBusTransportType.AmqpTcp
    };

    public ServiceBusAdministrationClient ManagementClient => _managementClient.Value;

    public ServiceBusClient BusClient => _busClient.Value;

    public ValueTask DisposeAsync()
    {
        if (_busClient.IsValueCreated)
        {
            return _busClient.Value.DisposeAsync();
        }

        return ValueTask.CompletedTask;
    }

    protected override void tryBuildSystemEndpoints(IWolverineRuntime runtime)
    {
        if (!SystemQueuesEnabled) return;
        
        var queueName = $"wolverine.response.{runtime.DurabilitySettings.AssignedNodeNumber}";

        var queue = Queues[queueName];

        queue.Options.AutoDeleteOnIdle = 5.Minutes();
        queue.Mode = EndpointMode.BufferedInMemory;
        queue.IsListener = true;
        queue.EndpointName = ResponseEndpointName;
        queue.IsUsedForReplies = true;
        queue.Role = EndpointRole.System;


        var retryName = SanitizeIdentifier($"wolverine.retries.{runtime.Options.ServiceName}".ToLower());
        var retryQueue = Queues[retryName];
        retryQueue.Mode = EndpointMode.BufferedInMemory;
        retryQueue.IsListener = true;
        retryQueue.EndpointName = RetryEndpointName;
        retryQueue.Role = EndpointRole.System;

        RetryQueue = retryQueue;

    }

    internal AzureServiceBusQueue? RetryQueue { get; set; }

    protected override IEnumerable<Endpoint> explicitEndpoints()
    {
        foreach (var queue in Queues) yield return queue;

        foreach (var topic in Topics) yield return topic;

        foreach (var subscription in Subscriptions) yield return subscription;
    }

    protected override IEnumerable<AzureServiceBusEndpoint> endpoints()
    {
        var dlqNames = Queues.Select(x => x.DeadLetterQueueName).Where(x => x.IsNotEmpty()).Distinct().ToArray();
        foreach (var dlqName in dlqNames)
        {
            Queues.FillDefault(dlqName!);
        }
        
        foreach (var queue in Queues) yield return queue;

        foreach (var topic in Topics) yield return topic;

        foreach (var subscription in Subscriptions) yield return subscription;
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
                    if (subscription != null)
                    {
                        return subscription;
                    }

                    var subscriptionName = uri.Segments.Last().TrimEnd('/');
                    var topic = Topics[topicName];
                    subscription = new AzureServiceBusSubscription(this, topic, subscriptionName);
                    Subscriptions.Add(subscription);

                    return subscription;
                }

                return Topics[topicName];
        }

        throw new ArgumentOutOfRangeException(nameof(uri));
    }

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

    private ServiceBusClient createServiceBusClient()
    {
        if (FullyQualifiedNamespace.IsNotEmpty() && TokenCredential != null)
        {
            return new ServiceBusClient(FullyQualifiedNamespace, TokenCredential, ClientOptions);
        }

        if (FullyQualifiedNamespace.IsNotEmpty() && NamedKeyCredential != null)
        {
            return new ServiceBusClient(FullyQualifiedNamespace, NamedKeyCredential, ClientOptions);
        }

        if (FullyQualifiedNamespace.IsNotEmpty() && SasCredential != null)
        {
            return new ServiceBusClient(FullyQualifiedNamespace, SasCredential, ClientOptions);
        }

        return new ServiceBusClient(ConnectionString, ClientOptions);
    }
    private ServiceBusAdministrationClient createServiceBusAdministrationClient()
    {
        if (FullyQualifiedNamespace.IsNotEmpty() && TokenCredential != null)
        {
            return new ServiceBusAdministrationClient(FullyQualifiedNamespace, TokenCredential);
        }

        if (FullyQualifiedNamespace.IsNotEmpty() && NamedKeyCredential != null)
        {
            return new ServiceBusAdministrationClient(FullyQualifiedNamespace, NamedKeyCredential);
        }

        if (FullyQualifiedNamespace.IsNotEmpty() && SasCredential != null)
        {
            return new ServiceBusAdministrationClient(FullyQualifiedNamespace, SasCredential);
        }

        return new ServiceBusAdministrationClient(ConnectionString);
    }
}