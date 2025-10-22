using Azure;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AzureServiceBus;

public partial class AzureServiceBusTransport : BrokerTransport<AzureServiceBusEndpoint>, IAsyncDisposable
{
    public const string ProtocolName = "asb";
    public const string ResponseEndpointName = "AzureServiceBusResponses";
    public const string RetryEndpointName = "AzureServiceBusRetries";
    private readonly Lazy<ServiceBusClient> _busClient;
    private readonly Lazy<ServiceBusAdministrationClient> _managementClient;

    public readonly List<AzureServiceBusSubscription> Subscriptions = new();
    private string _hostName;
    public const string DeadLetterQueueName = "wolverine-dead-letter-queue";

    public AzureServiceBusTransport() : this(ProtocolName)
    {

    }

    public AzureServiceBusTransport(string protocolName) : base(protocolName, "Azure Service Bus")
    {
        Queues = new(name => new AzureServiceBusQueue(this, name));
        Topics = new(name => new AzureServiceBusTopic(this, name));

        _managementClient =
            new Lazy<ServiceBusAdministrationClient>(createServiceBusAdministrationClient);
        _busClient = new Lazy<ServiceBusClient>(createServiceBusClient);

        IdentifierDelimiter = ".";
    }

    public async Task DeleteAllObjectsAsync()
    {
        var topics = _managementClient.Value.GetTopicsAsync();
        await foreach (var topic in topics)
        {
            await _managementClient.Value.DeleteTopicAsync(topic.Name);
        }

        var queues = _managementClient.Value.GetQueuesAsync();
        await foreach (var queue in queues)
        {
            await _managementClient.Value.DeleteQueueAsync(queue.Name);
        }
    }

    public override Uri ResourceUri
    {
        get
        {
            var uri = new Uri($"{ProtocolName}://");

            if (FullyQualifiedNamespace.IsNotEmpty())
            {
                uri = new Uri(uri, FullyQualifiedNamespace);
            }

            return uri;
        }
    }

    public override string SanitizeIdentifier(string identifier)
    {
        return identifier.ToLowerInvariant();
    }

    internal LightweightCache<string, AzureServiceBusTenant> Tenants { get; } = new(key => new AzureServiceBusTenant(key));

    /// <summary>
    /// Is this transport connection allowed to build and use response, retry, and control queues
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

    public async Task WithManagementClientAsync(Func<ServiceBusAdministrationClient, Task> action)
    {
        await action(_managementClient.Value);

        foreach (var tenant in Tenants)
        {
            tenant.Transport.NamedKeyCredential ??= NamedKeyCredential;
            tenant.Transport.SasCredential ??= SasCredential;
            tenant.Transport.TokenCredential ??= TokenCredential;
            await tenant.Transport.WithManagementClientAsync(action);
        }
    }

    public async Task WithServiceBusClientAsync(Func<ServiceBusClient, Task> action)
    {
        await action(BusClient);
        
        foreach (var tenant in Tenants)
        {
            tenant.Transport.NamedKeyCredential ??= NamedKeyCredential;
            tenant.Transport.SasCredential ??= SasCredential;
            tenant.Transport.TokenCredential ??= TokenCredential;
            await tenant.Transport.WithServiceBusClientAsync(action);
        }
    }
    
    public ServiceBusClient BusClient => _busClient.Value;


    public ServiceBusAdministrationClient ManagementClient => _managementClient.Value;

    public async ValueTask DisposeAsync()
    {
        if (_busClient.IsValueCreated)
        {
            await _busClient.Value.DisposeAsync();
        }

        foreach (var tenant in Tenants)
        { 
            await tenant.Transport.DisposeAsync();
        }
    }

    protected override void tryBuildSystemEndpoints(IWolverineRuntime runtime)
    {
        if (!SystemQueuesEnabled) return;

        var queueName = $"wolverine.response.{runtime.Options.ServiceName}.{runtime.DurabilitySettings.AssignedNodeNumber}";

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

    public override Endpoint? ReplyEndpoint()
    {
        var replies = base.ReplyEndpoint();
        if (replies is AzureServiceBusQueue) return replies;

        return null;
    }

    internal AzureServiceBusQueue? RetryQueue { get; set; }

    public string HostName
    {
        get
        {
            if (_hostName == null)
            {
                var parts = ConnectionString.Split(';');
                foreach (var part in parts)
                {
                    var split = part.Split('=');
                    if (split[0].EqualsIgnoreCase("Endpoint"))
                    {
                        _hostName = new Uri(split[1]).Host;
                    }
                }
            }

            return _hostName;
        }
    }

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