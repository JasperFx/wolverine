using System.Diagnostics;
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
        new Uri($"{AzureServiceBusTransport.ProtocolName}://queue/{queueName}"), role)
    {
        if (parent == null)
        {
            throw new ArgumentNullException(nameof(parent));
        }

        QueueName = EndpointName = queueName ?? throw new ArgumentNullException(nameof(queueName));
        Options = new CreateQueueOptions(QueueName);
    }

    public CreateQueueOptions Options { get; }

    public string QueueName { get; }

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
        if (!exists)
        {
            Options.Name = QueueName;

            await client.CreateQueueAsync(Options);
        }
    }

    public async ValueTask PurgeAsync(ILogger logger)
    {
        var client = Parent.BusClient;

        try
        {
            var receiver = client.CreateReceiver(QueueName);

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
            logger.LogError(e, "Error trying to purge Azure Service Bus queue {Queue}", QueueName);
        }
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

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        var messageReceiver = Parent.BusClient.CreateReceiver(QueueName);
        var mapper = BuildMapper(runtime);
        var listener = new BatchedAzureServiceBusListener(this, runtime.Logger, receiver, messageReceiver, mapper);

        return ValueTask.FromResult<IListener>(listener);
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        var mapper = BuildMapper(runtime);
        var sender = Parent.BusClient.CreateSender(QueueName);
        var protocol = new AzureServiceBusSenderProtocol(runtime, this, mapper, sender);

        return new BatchedSender(Uri, protocol, runtime.DurabilitySettings.Cancellation, runtime.Logger);
    }
}