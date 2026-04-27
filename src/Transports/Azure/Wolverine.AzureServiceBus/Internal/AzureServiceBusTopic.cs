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
using Wolverine.Runtime.Interop.MassTransit;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util;

namespace Wolverine.AzureServiceBus.Internal;

public class AzureServiceBusTopic : AzureServiceBusEndpoint, IMassTransitInteropEndpoint
{
    private bool _hasInitialized;

    public AzureServiceBusTopic(AzureServiceBusTransport parent, string topicName) : base(parent,
        new Uri($"{parent.Protocol}://topic/{Uri.EscapeDataString(topicName)}"), EndpointRole.Application)
    {
        if (parent == null)
        {
            throw new ArgumentNullException(nameof(parent));
        }

        TopicName = EndpointName = topicName ?? throw new ArgumentNullException(nameof(topicName));
        Options = new CreateTopicOptions(TopicName);
        BrokerRole = "topic";
    }

    public string TopicName { get; }

    /// <summary>
    /// Used by OptionsDescription to render references to this topic (for example
    /// from <see cref="AzureServiceBusSubscription.Topic"/>) as just the topic name.
    /// </summary>
    public override string ToString() => TopicName;

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

    [ChildDescription]
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

    public override bool IsPartitioned { get => Options.EnablePartitioning; }

    internal void UseNServiceBusInterop()
    {
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

            m.MapProperty(x => x.MessageType!, (e, msg) =>
            {
                if (msg.ApplicationProperties.TryGetValue("NServiceBus.EnclosedMessageTypes", out var raw))
                {
                    var typeName = (raw is byte[] b ? Encoding.UTF8.GetString(b) : raw.ToString())!;
                    if (typeName.IsNotEmpty())
                    {
                        var messageType = Type.GetType(typeName);
                        e.MessageType = messageType!.ToMessageTypeName();
                    }
                }
            },
                (e, msg) =>
            {
                msg.ApplicationProperties["NServiceBus.EnclosedMessageTypes"] = e.Message!.GetType().ToMessageTypeName();
            });
        });
    }

    Uri? IMassTransitInteropEndpoint.MassTransitUri()
    {
        return new Uri($"sb://{Parent.HostName}/topic/{TopicName}");
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
}