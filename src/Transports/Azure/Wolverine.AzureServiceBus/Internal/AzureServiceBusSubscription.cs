using System.Diagnostics;
using Azure.Messaging.ServiceBus.Administration;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AzureServiceBus.Internal;

public class AzureServiceBusSubscription : AzureServiceBusEndpoint, IBrokerQueue
{
    private bool _hasInitialized;

    public AzureServiceBusSubscription(AzureServiceBusTransport parent, AzureServiceBusTopic topic,
        string subscriptionName) : base(parent,
        new Uri($"{AzureServiceBusTransport.ProtocolName}://topic/{topic.TopicName}/{subscriptionName}"),
        EndpointRole.Application)
    {
        if (parent == null)
        {
            throw new ArgumentNullException(nameof(parent));
        }

        SubscriptionName = EndpointName = subscriptionName;
        Topic = topic ?? throw new ArgumentNullException(nameof(topic));

        Options = new CreateSubscriptionOptions(Topic.TopicName, SubscriptionName);
    }
    
    public CreateSubscriptionOptions Options { get; }

    public string SubscriptionName { get; }

    public AzureServiceBusTopic Topic { get; }

    public override async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        var requeue = Parent.RetryQueue != null ? Parent.RetryQueue.BuildInlineSender(runtime) : Topic.BuildInlineSender(runtime);
        var mapper = BuildMapper(runtime);
        
        if (Mode == EndpointMode.Inline)
        {
            var messageProcessor = Parent.BusClient.CreateProcessor(Topic.TopicName, SubscriptionName);
            var inlineListener = new InlineAzureServiceBusListener(this,
                runtime.LoggerFactory.CreateLogger<InlineAzureServiceBusListener>(), messageProcessor, receiver, mapper,  requeue
            );
        
            await inlineListener.StartAsync();
        
            return inlineListener;
        }
        
        var messageReceiver = Parent.BusClient.CreateReceiver(Topic.TopicName, SubscriptionName);
        
        var listener = new BatchedAzureServiceBusListener(this, runtime.LoggerFactory.CreateLogger<BatchedAzureServiceBusListener>(), receiver, messageReceiver, mapper, requeue);
        
        return listener;
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        throw new NotSupportedException();
    }

    public override async ValueTask<bool> CheckAsync()
    {
        var client = Parent.ManagementClient;

        return await client.SubscriptionExistsAsync(Topic.TopicName, SubscriptionName);
    }

    public override async ValueTask TeardownAsync(ILogger logger)
    {
        var client = Parent.ManagementClient;
        await client.DeleteSubscriptionAsync(Topic.TopicName, SubscriptionName);
    }

    public override ValueTask SetupAsync(ILogger logger)
    {
        var client = Parent.ManagementClient;
        return SetupAsync(client, logger);
    }

    internal async ValueTask SetupAsync(ServiceBusAdministrationClient client, ILogger logger)
    {
        var exists = await client.SubscriptionExistsAsync(Topic.TopicName, SubscriptionName);
        if (!exists)
        {
            Options.SubscriptionName = SubscriptionName;
            Options.TopicName = Topic.TopicName;

            await client.CreateSubscriptionAsync(Options);
        }
    }

    public async ValueTask PurgeAsync(ILogger logger)
    {
        try
        {
            var receiver = Parent.BusClient.CreateReceiver(Topic.TopicName, SubscriptionName);

            var stopwatch = new Stopwatch();
            stopwatch.Start();
            while (stopwatch.ElapsedMilliseconds < 2000)
            {
                var messages = await receiver.ReceiveMessagesAsync(25, 1.Seconds());
                if (!messages.Any())
                {
                    return;
                }

                foreach (var message in messages) await receiver.CompleteMessageAsync(message);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error trying to purge Azure Service Bus subscription {SubscriptionName} for topic {TopicName}", SubscriptionName, Topic.TopicName);
        }
    }

    public async ValueTask<Dictionary<string, string>> GetAttributesAsync()
    {
        var client = Parent.ManagementClient;
        var props = await client.GetSubscriptionAsync(Topic.TopicName, SubscriptionName);
        return new Dictionary<string, string>
        {
            { "TopicName", Topic.TopicName },
            { "SubscriptionName", SubscriptionName },
            
            { nameof(SubscriptionProperties.Status), props.Value.Status.ToString() },
        };
    }

    public override async ValueTask InitializeAsync(ILogger logger)
    {
        if (_hasInitialized)
        {
            return;
        }

        var client = Parent.ManagementClient;
        await InitializeAsync(client, logger);

        _hasInitialized = true;
    }

    internal ValueTask InitializeAsync(ServiceBusAdministrationClient client, ILogger logger)
    {
        if (Parent.AutoProvision)
        {
            return SetupAsync(client, logger);
        }

        return ValueTask.CompletedTask;
    }
}