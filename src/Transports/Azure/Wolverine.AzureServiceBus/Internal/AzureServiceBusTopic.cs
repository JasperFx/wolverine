using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AzureServiceBus.Internal;

public class AzureServiceBusTopic : AzureServiceBusEndpoint
{
    private bool _hasInitialized;

    public AzureServiceBusTopic(AzureServiceBusTransport parent, string topicName) : base(parent,
        new Uri($"{parent.Protocol}://topic/{topicName}"), EndpointRole.Application)
    {
        if (parent == null)
        {
            throw new ArgumentNullException(nameof(parent));
        }

        TopicName = EndpointName = topicName ?? throw new ArgumentNullException(nameof(topicName));
        Options = new CreateTopicOptions(TopicName);
    }

    public override Task<ServiceBusSessionReceiver> AcceptNextSessionAsync(CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public string TopicName { get; }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        throw new NotSupportedException();
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        return Parent.CreateSender(runtime, this);
    }

    public override async ValueTask<bool> CheckAsync()
    {
        var exists = true;

        await Parent.WithManagementClientAsync(async client =>
        {
            exists = exists && (await client.TopicExistsAsync(TopicName)).Value;
        });

        return exists;
    }

    public override ValueTask TeardownAsync(ILogger logger)
    {
        return new ValueTask(Parent.WithManagementClientAsync(client => client.DeleteTopicAsync(TopicName)));
    }

    public CreateTopicOptions Options { get; }

    public override ValueTask SetupAsync(ILogger logger)
    {
        return new ValueTask(Parent.WithManagementClientAsync(c => SetupAsync(c, logger)));
    }

    internal async Task SetupAsync(ServiceBusAdministrationClient client, ILogger logger)
    {
        var exists = await client.TopicExistsAsync(TopicName, CancellationToken.None);
        if (!exists)
        {
            Options.Name = TopicName;

            try
            {
                await client.CreateTopicAsync(Options);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error trying to initialize topic {Name}", TopicName);
            }
        }
    }

    public override async ValueTask InitializeAsync(ILogger logger)
    {
        if (_hasInitialized)
        {
            return;
        }

        await Parent.WithManagementClientAsync(client => InitializeAsync(client, logger).AsTask());

        _hasInitialized = true;
    }

    internal ValueTask InitializeAsync(ServiceBusAdministrationClient client, ILogger logger)
    {
        if (Parent.AutoProvision)
        {
            return new ValueTask(SetupAsync(client, logger));
        }

        return ValueTask.CompletedTask;
    }

    public AzureServiceBusSubscription FindOrCreateSubscription(string subscriptionName)
    {
        var existing =
            Parent.Subscriptions.FirstOrDefault(x => x.SubscriptionName == subscriptionName && x.Topic == this);

        if (existing != null)
        {
            return existing;
        }

        var subscription = new AzureServiceBusSubscription(Parent, this, subscriptionName);
        Parent.Subscriptions.Add(subscription);

        return subscription;
    }
}