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
    /// When true, this topic belongs to another application. Wolverine will never create, check-and-create
    /// or delete it, regardless of <c>AutoProvision()</c> or resource setup, but subscriptions on it are
    /// still provisioned. If the topic does not exist, provisioning a subscription fails with an error
    /// naming the topic. See https://github.com/JasperFx/wolverine/issues/5342.
    /// </summary>
    public bool IsExternallyOwned { get; set; }

    /// <summary>
    /// Used by OptionsDescription to render references to this topic (for example
    /// from <see cref="AzureServiceBusSubscription.Topic"/>) as just the topic name.
    /// </summary>
    public override string ToString() => TopicName;

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        throw new NotSupportedException(
            $"The Azure Service Bus topic '{TopicName}' ({Uri}) is a publish-only endpoint. Azure Service Bus topics cannot be listened to directly. Listen to one of the topic's subscriptions instead with ListenToAzureServiceBusSubscription(subscriptionName).FromTopic(\"{TopicName}\").");
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
        if (IsExternallyOwned) return ValueTask.CompletedTask;

        return new ValueTask(Parent.WithManagementClientAsync(client => client.DeleteTopicAsync(TopicName)));
    }

    [ChildDescription]
    public CreateTopicOptions Options { get; }

    public override ValueTask SetupAsync(ILogger logger)
    {
        return new ValueTask(Parent.WithManagementClientAsync((c, transport) => setupAsync(c, logger, transport)));
    }

    internal Task SetupAsync(ServiceBusAdministrationClient client, ILogger logger)
    {
        return setupAsync(client, logger, Parent);
    }

    // The namespaces (this transport or a tenant's) where this topic was created by this process
    private readonly HashSet<AzureServiceBusTransport> _createdIn = new();

    /// <summary>
    /// True when this process created the topic in that namespace, so none of its subscriptions can exist yet
    /// </summary>
    internal bool WasCreatedIn(AzureServiceBusTransport transport)
    {
        lock (_createdIn) return _createdIn.Contains(transport);
    }

    private async Task setupAsync(ServiceBusAdministrationClient client, ILogger logger, AzureServiceBusTransport transport)
    {
        if (await SetupAsync(client, logger, transport.TargetsEmulator))
        {
            lock (_createdIn) _createdIn.Add(transport);
        }
    }

    /// <param name="createFirst">
    /// Create without checking for existence first, and take "already exists" (409) as the answer. Only for the
    /// emulator, where an existence check for a topic that is not there takes about 0.7 seconds, a create about 0.25,
    /// and a create of a topic that already exists a few milliseconds. A real namespace keeps the existence check first
    /// </param>
    /// <returns>Whether this call created the topic</returns>
    internal async Task<bool> SetupAsync(ServiceBusAdministrationClient client, ILogger logger, bool createFirst)
    {
        if (IsExternallyOwned) return false;

        if (!createFirst && (await client.TopicExistsAsync(TopicName, CancellationToken.None)).Value) return false;

        Options.Name = TopicName;

        try
        {
            await client.CreateTopicAsync(Options);
            return true;
        }
        catch (ServiceBusException e) when (createFirst && e.Reason == ServiceBusFailureReason.MessagingEntityAlreadyExists)
        {
            // It exists, which is all the existence check would have said
            return false;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error trying to initialize topic {Name}", TopicName);
            return false;
        }
    }

    public override async ValueTask InitializeAsync(ILogger logger)
    {
        if (_hasInitialized)
        {
            return;
        }

        await Parent.WithManagementClientAsync((client, transport) => initializeAsync(client, logger, transport));

        _hasInitialized = true;
    }

    internal ValueTask InitializeAsync(ServiceBusAdministrationClient client, ILogger logger)
    {
        return new ValueTask(initializeAsync(client, logger, Parent));
    }

    private Task initializeAsync(ServiceBusAdministrationClient client, ILogger logger, AzureServiceBusTransport transport)
    {
        if (Parent.AutoProvision && !IsExternallyOwned)
        {
            return setupAsync(client, logger, transport);
        }

        return Task.CompletedTask;
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

    // Type resolution from the NServiceBus.EnclosedMessageTypes header is not AOT-clean; the reflection and
    // its IL2057 suppression live in NServiceBusInterop.ResolveMessageType, next to the call.
    internal void UseNServiceBusInterop()
    {
        // GH-5244: XML bodies are read too, and sent instead of JSON when the endpoint\'s default serializer is
        // an NServiceBusXmlSerializer
        NServiceBusInterop.UseNServiceBusSerializers(this, new NewtonsoftSerializer(new JsonSerializerSettings()));
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

            // NServiceBus picks its deserializer from the NServiceBus.ContentType header, so stamp it next to
            // the native content type, and prefer it on the way in
            m.MapProperty(x => x.ContentType!, (e, msg) =>
                {
                    e.ContentType = msg.ApplicationProperties.TryGetValue(NServiceBusInterop.ContentTypeHeader, out var raw) && raw != null
                        ? raw is byte[] b ? Encoding.UTF8.GetString(b) : raw.ToString()
                        : msg.ContentType;
                },
                (e, msg) =>
                {
                    msg.ContentType = e.ContentType;
                    if (e.ContentType != null)
                    {
                        msg.ApplicationProperties[NServiceBusInterop.ContentTypeHeader] = e.ContentType;
                    }
                });

            m.MapProperty(x => x.MessageType!, (e, msg) =>
            {
                if (msg.ApplicationProperties.TryGetValue(NServiceBusInterop.EnclosedMessageTypesHeader, out var raw))
                {
                    var header = raw is byte[] b ? Encoding.UTF8.GetString(b) : raw?.ToString();
                    if (NServiceBusInterop.ResolveMessageType(header) is string messageType)
                    {
                        e.MessageType = messageType;
                    }
                }
            },
                (e, msg) =>
            {
                msg.ApplicationProperties[NServiceBusInterop.EnclosedMessageTypesHeader] = e.Message!.GetType().ToMessageTypeName();
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