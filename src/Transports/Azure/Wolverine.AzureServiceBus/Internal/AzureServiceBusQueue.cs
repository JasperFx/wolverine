using System.Diagnostics;
using System.Text;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Newtonsoft;
using Wolverine.Runtime.Interop.MassTransit;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util;

namespace Wolverine.AzureServiceBus.Internal;

public class AzureServiceBusQueue : AzureServiceBusEndpoint, IBrokerQueue, IMassTransitInteropEndpoint
{
    private bool _hasInitialized;

    public AzureServiceBusQueue(AzureServiceBusTransport parent, string queueName,
        EndpointRole role = EndpointRole.Application) : base(parent,
        new Uri($"{parent.Protocol}://queue/{Uri.EscapeDataString(queueName)}"), role)
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
        BrokerRole = "queue";
    }

    [ChildDescription]
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

    public override bool IsPartitioned { get => Options.EnablePartitioning; }

    internal override bool RequiresSessions => Options.RequiresSession;

    /// <summary>
    /// GH-4051. Azure Service Bus qualifies on both counts <see cref="EndpointMode.NativeAck" /> requires.
    /// Settlement is per message -- <c>ServiceBusReceiver.CompleteMessageAsync</c> takes one message and settles
    /// exactly that lock, with no cumulative or ordered semantics anywhere in the peek-lock model -- and settling
    /// out of delivery order is expressible, because a lock is held per message rather than over a position in a
    /// stream. That is the difference from Kafka, which cannot express a gap in a cumulative offset commit.
    ///
    /// <para>
    /// Only queues and subscriptions, never topics: native acks are a listening concept and a topic is only ever
    /// published to. <see cref="AzureServiceBusSubscription" /> declares this separately rather than inheriting it
    /// from a shared base, so that the topic cannot pick it up by accident.
    /// </para>
    ///
    /// <para>
    /// Sessions are the one Azure Service Bus configuration this mode cannot serve, and they are refused after
    /// compilation rather than here -- see <see cref="AzureServiceBusEndpoint.validateModeConfiguration" />.
    /// </para>
    /// </summary>
    protected override bool supportsNativeAck => true;

    /// <summary>
    /// GH-4048. An unsettled Azure Service Bus delivery is locked only for the queue's <c>LockDuration</c>, after
    /// which the broker hands the message to someone else -- so a NativeAck endpoint here has to renew that lock for
    /// as long as the envelope sits in an execution lane. <see cref="BatchedAzureServiceBusListener" /> supplies the
    /// renewal through <see cref="Wolverine.Transports.ISupportLeaseRenewal" />.
    /// </summary>
    protected internal override bool holdsExpiringLease => true;

    internal override TimeSpan LockDuration => Options.LockDuration;

    private async Task purgeWithSessions(ServiceBusClient client)
    {
        using var cancellation = new CancellationTokenSource();
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
        var dlq = Parent.Queues[DeadLetterQueueName!];
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

    // Buffered/durable queues move failures to the managed dead letter queue; inline queues use the
    // native $DeadLetterQueue sub-queue. Either way it's a native broker destination unless dead
    // lettering was explicitly disabled (DeadLetterQueueName set to null), which falls back to
    // Wolverine's durable storage.
    public override DeadLetterStorageMode DeadLetterStorage => DeadLetterQueueName.IsNotEmpty()
        ? DeadLetterStorageMode.Native
        : DeadLetterStorageMode.Durable;
    
    // NServiceBus interop: NSB writes the .NET assembly-qualified type name
    // to the message header; NServiceBusInterop.ResolveMessageType turns that
    // into a Wolverine message type name. Type resolution from a runtime string
    // is fundamentally not AOT-clean — the trimmer can't know which types may
    // appear — so the reflection and its IL2057 suppression live there, next to
    // the call. AOT-clean apps using NSB interop preserve their NSB-side message
    // types via TrimmerRootDescriptor.
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
                    var queueName = (raw is byte[] b ? Encoding.UTF8.GetString(b) : raw.ToString())!;
                    e.ReplyUri = new Uri($"{Parent.Protocol}://queue/{queueName}");
                }
            }

            m.MapProperty(x => x.ReplyUri!, ReadReplyUri, WriteReplyToAddress);
            
            m.MapProperty(x => x.MessageType!, (e, m) =>
            {
                // Incoming
                if (m.ApplicationProperties.TryGetValue(NServiceBusInterop.EnclosedMessageTypesHeader, out var raw))
                {
                    var header = raw is byte[] b ? Encoding.UTF8.GetString(b) : raw?.ToString();
                    if (NServiceBusInterop.ResolveMessageType(header) is string messageType)
                    {
                        e.MessageType = messageType;
                    }
                }
            },
                (e, m) =>
            {
                // Outgoing, use the interop strategy here
                m.ApplicationProperties[NServiceBusInterop.EnclosedMessageTypesHeader] = e.Message!.GetType().ToMessageTypeName();
            });
        });
    }

    Uri? IMassTransitInteropEndpoint.MassTransitUri()
    {
        return new Uri($"sb://{Parent.HostName}/{QueueName}");
    }

    Uri? IMassTransitInteropEndpoint.MassTransitReplyUri()
    {
        return Parent.ReplyEndpoint()!.As<IMassTransitInteropEndpoint>().MassTransitUri();
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

    public async Task<long> QueuedCountAsync()
    {
        long value = 0;
        await Parent.WithManagementClientAsync(async client =>
        {
            var runtime = await client.GetQueueRuntimePropertiesAsync(QueueName);

            value = runtime.Value.ActiveMessageCount;
        });

        return value;
    }
}