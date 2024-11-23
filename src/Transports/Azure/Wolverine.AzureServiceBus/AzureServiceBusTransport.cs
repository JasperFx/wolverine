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

    public override string SanitizeIdentifier(string identifier)
    {
        return identifier.ToLowerInvariant();
    }

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
        // TODO -- gets fancier later with multi-tenancy
        await action(_managementClient.Value);
    }

    public async Task WithServiceBusClientAsync(Func<ServiceBusClient, Task> action)
    {
        // TODO -- gets fancier later with multi-tenancy
        await action(BusClient);
    }
    
    public ServiceBusClient BusClient => _busClient.Value;

    internal ISender CreateSender(IWolverineRuntime runtime, AzureServiceBusTopic topic)
    {
        // TODO -- gets fancier later with multi-tenancy
        
        var mapper = topic.BuildMapper(runtime);
        
        var sender = BusClient.CreateSender(topic.TopicName);

        if (topic.Mode == EndpointMode.Inline)
        {
            var inlineSender = new InlineAzureServiceBusSender(topic, mapper, sender,
                runtime.LoggerFactory.CreateLogger<InlineAzureServiceBusSender>(), runtime.Cancellation);

            return inlineSender;
        }

        var protocol = new AzureServiceBusSenderProtocol(runtime, topic, mapper, sender);

        return new BatchedSender(topic, protocol, runtime.DurabilitySettings.Cancellation, runtime.LoggerFactory.CreateLogger<AzureServiceBusSenderProtocol>());
    }

    internal ISender BuildInlineSender(IWolverineRuntime runtime, AzureServiceBusTopic topic)
    {
        // TODO -- gets fancier with multi-tenancy
        var mapper = topic.BuildMapper(runtime);
        var sender = BusClient.CreateSender(topic.TopicName);
        return new InlineAzureServiceBusSender(topic, mapper, sender,
            runtime.LoggerFactory.CreateLogger<InlineAzureServiceBusSender>(), runtime.Cancellation);

    }
    
    internal ISender BuildInlineSender(IWolverineRuntime runtime, AzureServiceBusQueue queue)
    {
        // TODO -- gets fancier with multi-tenancy
        var mapper = queue.BuildMapper(runtime);
        var sender = BusClient.CreateSender(queue.QueueName);
        return new InlineAzureServiceBusSender(queue, mapper, sender,
            runtime.LoggerFactory.CreateLogger<InlineAzureServiceBusSender>(), runtime.Cancellation);

    }
    
    internal ISender BuildSender(IWolverineRuntime runtime, AzureServiceBusQueue queue)
    {
        // TODO -- get fancier for multi-tenancy
        var mapper = queue.BuildMapper(runtime);
        var sender = BusClient.CreateSender(queue.QueueName);

        if (queue.Mode == EndpointMode.Inline)
        {
            var inlineSender = new InlineAzureServiceBusSender(queue, mapper, sender,
                runtime.LoggerFactory.CreateLogger<InlineAzureServiceBusSender>(), runtime.Cancellation);

            return inlineSender;
        }

        var protocol = new AzureServiceBusSenderProtocol(runtime, queue, mapper, sender);

        return new BatchedSender(queue, protocol, runtime.DurabilitySettings.Cancellation, runtime.LoggerFactory.CreateLogger<AzureServiceBusSenderProtocol>());
    }
    
    internal Task<ServiceBusSessionReceiver> AcceptNextSessionAsync(AzureServiceBusQueue queue, CancellationToken cancellationToken)
    {
        return BusClient.AcceptNextSessionAsync(queue.QueueName, cancellationToken: cancellationToken);
    }
    
    internal Task<ServiceBusSessionReceiver> AcceptNextSessionAsync(AzureServiceBusSubscription subscription, CancellationToken cancellationToken)
    {
        return BusClient.AcceptNextSessionAsync(subscription.Topic.TopicName, subscription.SubscriptionName,
            cancellationToken: cancellationToken);
    }
    
    internal async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver, AzureServiceBusQueue queue)
    {
        // TODO -- gets fancier with multi-tenancy
        
        var mapper = queue.BuildMapper(runtime);

        var requeue = queue.BuildInlineSender(runtime);

        if (queue.Options.RequiresSession)
        {
            return new AzureServiceBusSessionListener(queue, receiver, mapper,
                runtime.LoggerFactory.CreateLogger<AzureServiceBusSessionListener>(), requeue);
        }

        if (queue.Mode == EndpointMode.Inline)
        {
            var messageProcessor = BusClient.CreateProcessor(queue.QueueName);

            var inlineListener = new InlineAzureServiceBusListener(queue,
                runtime.LoggerFactory.CreateLogger<InlineAzureServiceBusListener>(), messageProcessor, receiver,
                mapper,
                requeue);

            await inlineListener.StartAsync();

            return inlineListener;
        }

        var messageReceiver = BusClient.CreateReceiver(queue.QueueName);
        var logger = runtime.LoggerFactory.CreateLogger<BatchedAzureServiceBusListener>();
        var listener = new BatchedAzureServiceBusListener(queue, logger, receiver, messageReceiver, mapper, requeue);

        return listener;
    }
    
    public async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver, AzureServiceBusSubscription subscription)
    {
        var requeue = RetryQueue != null ? RetryQueue.BuildInlineSender(runtime) : BuildInlineSender(runtime, subscription.Topic);
        var mapper = subscription.BuildMapper(runtime);

        if (subscription.Options.RequiresSession)
        {
            return new AzureServiceBusSessionListener(subscription, receiver, mapper,
                runtime.LoggerFactory.CreateLogger<AzureServiceBusSessionListener>(), requeue);
        }

        if (subscription.Mode == EndpointMode.Inline)
        {
            var messageProcessor = BusClient.CreateProcessor(subscription.Topic.TopicName, subscription.SubscriptionName);
            var inlineListener = new InlineAzureServiceBusListener(subscription,
                runtime.LoggerFactory.CreateLogger<InlineAzureServiceBusListener>(), messageProcessor, receiver, mapper,  requeue
            );

            await inlineListener.StartAsync();

            return inlineListener;
        }

        var messageReceiver = BusClient.CreateReceiver(subscription.Topic.TopicName, subscription.SubscriptionName);

        var listener = new BatchedAzureServiceBusListener(subscription, runtime.LoggerFactory.CreateLogger<BatchedAzureServiceBusListener>(), receiver, messageReceiver, mapper, requeue);

        return listener;
    }

    public ServiceBusAdministrationClient ManagementClient => _managementClient.Value;

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

    public override Endpoint? ReplyEndpoint()
    {
        var replies = base.ReplyEndpoint();
        if (replies is AzureServiceBusQueue) return replies;

        return null;
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