using System.Diagnostics;
using System.Text;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Interop.MassTransit;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util;

namespace Wolverine.AzureServiceBus.Internal;

public class AzureServiceBusQueue : AzureServiceBusEndpoint, IBrokerQueue, IMassTransitInteropEndpoint
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

    public override async ValueTask<bool> CheckAsync()
    {
        var exists = true;

        await Parent.WithManagementClientAsync(async c => exists = exists && await c.QueueExistsAsync(QueueName));

        return exists;
    }

    public override async ValueTask TeardownAsync(ILogger logger)
    {
        await Parent.WithManagementClientAsync(c => c.DeleteQueueAsync(QueueName));
    }

    public override async ValueTask SetupAsync(ILogger logger)
    {
        await Parent.WithManagementClientAsync(async client =>
        {
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
        });
    }

    public async ValueTask PurgeAsync(ILogger logger)
    {
        await Parent.WithServiceBusClientAsync(async client =>
        {
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
        });
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
        var dict = new Dictionary<string, string>
        {
            { "Name", QueueName }
        };
        
        await Parent.WithManagementClientAsync(async client =>
        {
            var props = await client.GetQueueAsync(QueueName);
            dict[nameof(QueueProperties.Status)] = props.Value.Status.ToString();
        });

        return dict;
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
        return Parent.BuildListenerForQueue(runtime, receiver, this);
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        return Parent.BuildSenderForQueue(runtime, this);
    }

    /// <summary>
    /// Name of the dead letter queue for this ASB queue where failed messages will be moved
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
            deadLetterSender = Parent.BuildInlineSenderForQueue(runtime, dlq);
            return true;
        }

        deadLetterSender = default;
        return false;
    }
    
    internal void UseNServiceBusInterop()
    {
        // NServiceBus.EnclosedMessageTypes
        DefaultSerializer = new NewtonsoftSerializer(new JsonSerializerSettings());
        customizeMapping((m, _) =>
        {
            m.MapPropertyToHeader(x => x.ConversationId, "NServiceBus.ConversationId");
            m.MapPropertyToHeader(x => x.SentAt, "NServiceBus.TimeSent");
            m.MapPropertyToHeader(x => x.CorrelationId!, "NServiceBus.CorrelationId");

            var replyAddress = new Lazy<string>(() =>
            {
                var replyEndpoint = Parent.ReplyEndpoint() as AzureServiceBusQueue;

                return replyEndpoint?.QueueName ?? string.Empty;
            });

            void WriteReplyToAddress(Envelope e, ServiceBusMessage props)
            {
                props.ApplicationProperties["NServiceBus.ReplyToAddress"] = replyAddress.Value;
            }

            void ReadReplyUri(Envelope e, ServiceBusReceivedMessage serviceBusReceivedMessage)
            {
                if (serviceBusReceivedMessage.ApplicationProperties.TryGetValue("NServiceBus.ReplyToAddress",
                        out var raw))
                {
                    var queueName = (raw is byte[] b ? Encoding.Default.GetString(b) : raw.ToString())!;
                    e.ReplyUri = new Uri($"{Parent.Protocol}://queue/{queueName}");
                }
            }

            m.MapProperty(x => x.ReplyUri!, ReadReplyUri, WriteReplyToAddress);
            
            m.MapProperty(x => x.MessageType, (e, m) =>
            {
                // Incoming  
                if (m.ApplicationProperties.TryGetValue("NServiceBus.EnclosedMessageTypes", out var raw))
                {
                    var typeName = (raw is byte[] b ? Encoding.Default.GetString(b) : raw.ToString())!;
                    if (typeName.IsNotEmpty())
                    {
                        var messageType = Type.GetType(typeName);
                        e.MessageType = messageType.ToMessageTypeName();
                    }
                }
            }, 
                (e, m) =>
            {
                // Outgoing, use the interop strategy here
                m.ApplicationProperties["NServiceBus.EnclosedMessageTypes"] = e.Message.GetType().ToMessageTypeName();
            });
        });
    }

    Uri? IMassTransitInteropEndpoint.MassTransitUri()
    {
        return new Uri($"sb://{Parent.HostName}/{QueueName}");
    }

    Uri? IMassTransitInteropEndpoint.MassTransitReplyUri()
    {
        return Parent.ReplyEndpoint().As<IMassTransitInteropEndpoint>().MassTransitUri();
    }

    Uri? IMassTransitInteropEndpoint.TranslateMassTransitToWolverineUri(Uri uri)
    {
        var lastSegment = uri.Segments.Last();
        return Parent.Queues[lastSegment].Uri;
    }

    internal void UseMassTransitInterop(Action<IMassTransitInterop>? configure = null)
    {
        customizeMapping((m, _) => m.InteropWithMassTransit(configure));
    }
}