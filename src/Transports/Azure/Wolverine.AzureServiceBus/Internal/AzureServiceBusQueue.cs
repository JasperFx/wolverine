using System.Diagnostics;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AzureServiceBus.Internal;

public class AzureServiceBusQueue : AzureServiceBusEndpoint, IBrokerQueue
{
    private bool _hasInitialized;

    public AzureServiceBusQueue(AzureServiceBusTransport parent, string queueName,
        EndpointRole role = EndpointRole.Application) : base(parent,
        new Uri($"{parent.Protocol}://queue/{queueName}"), role)
    {
        if (parent == null)
        {
            throw new ArgumentNullException(nameof(parent));
        }

        QueueName = EndpointName = queueName ?? throw new ArgumentNullException(nameof(queueName));
        Options = new CreateQueueOptions(QueueName)
        {
            DeadLetteringOnMessageExpiration = false
        };
    }

    public CreateQueueOptions Options { get; }

    public string QueueName { get; }

    public override Task<ServiceBusSessionReceiver> AcceptNextSessionAsync(CancellationToken cancellationToken)
    {
        return Parent.BusClient.AcceptNextSessionAsync(QueueName, cancellationToken: cancellationToken);
    }

    public override async ValueTask<bool> CheckAsync()
    {
        var client = Parent.ManagementClient;

        return await client.QueueExistsAsync(QueueName);
    }

    public override ValueTask TeardownAsync(ILogger logger)
    {
        var task = Parent.ManagementClient.DeleteQueueAsync(QueueName);
        return new ValueTask(task);
    }

    public override async ValueTask SetupAsync(ILogger logger)
    {
        var client = Parent.ManagementClient;

        var exists = await client.QueueExistsAsync(QueueName);
        if (!exists.Value)
        {
            Options.Name = QueueName;

            try
            {
                await client.CreateQueueAsync(Options);
            }
            catch (ServiceBusException e)
            {
                if (e.Reason == ServiceBusFailureReason.MessagingEntityAlreadyExists)
                {
                    return;
                }
                
                throw;
            }
        }
    }

    public async ValueTask PurgeAsync(ILogger logger)
    {
        var client = Parent.BusClient;

        try
        {
            if (Options.RequiresSession)
            {
                await purgeWithSessions(client);
            }
            else
            {
                await purgeWithoutSessions(client);
            }
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Error trying to purge Azure Service Bus queue {Queue}", QueueName);
        }
    }

    private async Task purgeWithSessions(ServiceBusClient client)
    {
        var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(2000);

        var stopwatch = new Stopwatch();
        stopwatch.Start();
        while (stopwatch.ElapsedMilliseconds < 2000)
        {
            var session = await client.AcceptNextSessionAsync(QueueName, cancellationToken: cancellation.Token);

            var messages = await session.ReceiveMessagesAsync(25, 1.Seconds(), cancellation.Token);
            foreach (var message in messages) await session.CompleteMessageAsync(message, cancellation.Token);
            while (messages.Any())
            {
                messages = await session.ReceiveMessagesAsync(25, 1.Seconds(), cancellation.Token);
                foreach (var message in messages) await session.CompleteMessageAsync(message, cancellation.Token);
            }
        }
    }

    private async Task<bool> purgeWithoutSessions(ServiceBusClient client)
    {
        var receiver = client.CreateReceiver(QueueName);

        var stopwatch = new Stopwatch();
        stopwatch.Start();
        while (stopwatch.ElapsedMilliseconds < 2000)
        {
            var messages = await receiver.ReceiveMessagesAsync(25, 1.Seconds());
            if (!messages.Any())
            {
                return true;
            }

            foreach (var message in messages) await receiver.CompleteMessageAsync(message);
        }

        return false;
    }

    public async ValueTask<Dictionary<string, string>> GetAttributesAsync()
    {
        var client = Parent.ManagementClient;
        QueueProperties props = await client.GetQueueAsync(QueueName);
        return new Dictionary<string, string>
        {
            { "Name", QueueName },
            { nameof(QueueProperties.Status), props.Status.ToString() }
        };
    }

    public override async ValueTask InitializeAsync(ILogger logger)
    {
        if (_hasInitialized)
        {
            return;
        }

        if (Parent.AutoProvision)
        {
            await SetupAsync(logger);
        }

        if (Parent.AutoPurgeAllQueues)
        {
            await PurgeAsync(logger);
        }

        _hasInitialized = true;
    }

    public override async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        var mapper = BuildMapper(runtime);

        var requeue = BuildInlineSender(runtime);

        if (Options.RequiresSession)
        {
            return new AzureServiceBusSessionListener(this, receiver, mapper,
                runtime.LoggerFactory.CreateLogger<AzureServiceBusSessionListener>(), requeue);
        }

        if (Mode == EndpointMode.Inline)
        {
            var messageProcessor = Parent.BusClient.CreateProcessor(QueueName);

            var inlineListener = new InlineAzureServiceBusListener(this,
                runtime.LoggerFactory.CreateLogger<InlineAzureServiceBusListener>(), messageProcessor, receiver,
                mapper,
                requeue);

            await inlineListener.StartAsync();

            return inlineListener;
        }

        var messageReceiver = Parent.BusClient.CreateReceiver(QueueName);
        var logger = runtime.LoggerFactory.CreateLogger<BatchedAzureServiceBusListener>();
        var listener = new BatchedAzureServiceBusListener(this, logger, receiver, messageReceiver, mapper, requeue);

        return listener;
    }

    internal ISender BuildInlineSender(IWolverineRuntime runtime)
    {
        var mapper = BuildMapper(runtime);
        var sender = Parent.BusClient.CreateSender(QueueName);
        return new InlineAzureServiceBusSender(this, mapper, sender,
            runtime.LoggerFactory.CreateLogger<InlineAzureServiceBusSender>(), runtime.Cancellation);

    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        var mapper = BuildMapper(runtime);
        var sender = Parent.BusClient.CreateSender(QueueName);

        if (Mode == EndpointMode.Inline)
        {
            var inlineSender = new InlineAzureServiceBusSender(this, mapper, sender,
                runtime.LoggerFactory.CreateLogger<InlineAzureServiceBusSender>(), runtime.Cancellation);

            return inlineSender;
        }

        var protocol = new AzureServiceBusSenderProtocol(runtime, this, mapper, sender);

        return new BatchedSender(this, protocol, runtime.DurabilitySettings.Cancellation, runtime.LoggerFactory.CreateLogger<AzureServiceBusSenderProtocol>());
    }

    /// <summary>
    /// Name of the dead letter queue for this SQS queue where failed messages will be moved
    /// </summary>
    public string? DeadLetterQueueName { get; set; } = AzureServiceBusTransport.DeadLetterQueueName;


    internal void ConfigureDeadLetterQueue(Action<AzureServiceBusQueue> configure)
    {
        var dlq = Parent.Queues[DeadLetterQueueName];
        configure(dlq);
    }

    public override bool TryBuildDeadLetterSender(IWolverineRuntime runtime, out ISender? deadLetterSender)
    {
        if (DeadLetterQueueName.IsNotEmpty())
        {
            var dlq = Parent.Queues[DeadLetterQueueName];
            deadLetterSender = dlq.BuildInlineSender(runtime);
            return true;
        }

        deadLetterSender = default;
        return false;
    }
}