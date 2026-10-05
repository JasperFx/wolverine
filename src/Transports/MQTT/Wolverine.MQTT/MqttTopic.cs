using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Protocol;
using Wolverine.Configuration;
using Wolverine.MQTT.Internals;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util;

namespace Wolverine.MQTT;

public class MqttTopic : Endpoint, ISender, ITopicEndpoint
{
    public const string WolverineTopicsName = "wolverine/topics";

    public string TopicName { get; }
    private CancellationToken _cancellation;

    public MqttTopic(string topicName, MqttTransport parent, EndpointRole role) : base(MqttEndpointUri.Topic(parent.Protocol, topicName), role)
    {
        TopicName = topicName.Trim('/');
        Parent = parent;
        
        if (TopicName.StartsWith("$share/"))
        {
            var parts = TopicName.Split('/');
            // Even if the shared topic format is technically invalid here,
            // we will still attempt to extract the listening topic.
            // Invalid formats should be caught by the broker or other validation.
            ListeningTopic = parts.Length >= 3 ? string.Join("/", parts.Skip(2)) : TopicName;
        }
        else
        {
            ListeningTopic = TopicName;
        }

        EndpointName = topicName;
        BrokerRole = "topic";

        EnvelopeMapper = new MqttEnvelopeMapper(this);
        Mode = EndpointMode.BufferedInMemory;
    }

    [IgnoreDescription]
    public MqttTransport Parent { get; }
    
    /// <summary>
    /// The topic string that is used to match against incoming messages.
    /// For a shared subscription like "$share/group/topic", this will be "topic".
    /// Otherwise, it is the same as TopicName.
    /// </summary>
    public string ListeningTopic { get; }

    /// <summary>
    /// When set, overrides the built in envelope mapping with a custom
    /// implementation
    /// </summary>
    [IgnoreDescription]
    public IMqttEnvelopeMapper EnvelopeMapper { get; set; }

    public override bool AutoStartSendingAgent()
    {
        return base.AutoStartSendingAgent() || RoutingType == RoutingMode.ByTopic;
    }

    public override async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        Compile(runtime);
        
        _cancellation = runtime.Cancellation;

        MessageTypeName = MessageType?.ToMessageTypeName();

        var logger = runtime.LoggerFactory.CreateLogger<MqttListener>();

        var listener = new MqttListener(Parent, logger, this, receiver, Parent.Client);
        await Parent.SubscribeToTopicAsync(TopicName, listener, this, Parent.Client);

        // Broker-per-tenant (GH-3307): the shared listener consumes the default connection. Each tenant runs its
        // own listener on its own dedicated client, stamping the tenant id onto inbound envelopes via
        // TenantIdRule. Per-envelope completion routes back over the receiving connection through
        // Envelope.Listener — the same CompoundListener multi-tenancy pattern used by RabbitMQ / NATS / SQS.
        if (Parent.Tenants.Any() && TenancyBehavior == TenancyBehavior.TenantAware)
        {
            var compound = new CompoundListener(Uri);
            compound.Inner.Add(listener);

            foreach (var tenant in Parent.Tenants)
            {
                var client = Parent.GetTenantClient(tenant);
                var tenantReceiver = new ReceiverWithRules(receiver, [new TenantIdRule(tenant.TenantId)]);
                var tenantListener = new MqttListener(Parent, logger, this, tenantReceiver, client);
                await Parent.SubscribeToTopicAsync(TopicName, tenantListener, this, client);
                compound.Inner.Add(tenantListener);
            }

            return compound;
        }

        return listener;
    }

    public string? MessageTypeName { get; private set; }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        Compile(runtime);

        // Broker-per-tenant (GH-3307): route by Envelope.TenantId to a per-tenant sender bound to that tenant's
        // own connection, falling back to the shared/default connection for the untenanted path.
        if (Parent.Tenants.Any() && TenancyBehavior == TenancyBehavior.TenantAware)
        {
            // GH-4820. Inline keeps the fire-and-forget MqttTopicSender, which is what inline means. Every
            // other mode gets the same acknowledgement-aware sender the untenanted path below has had since
            // the durable-send fix -- one BatchedSender per tenant connection, over a
            // CallbackAwareTenantedSender so each of them actually receives the ISenderCallback it needs to
            // settle the outbox. Before this, a tenanted endpoint with UseDurableOutbox() sent through
            // EnqueueAsync, which only reaches the managed client's in-memory queue: the send never threw,
            // the outbox deleted its row believing it had succeeded, and a restart lost the message.
            if (!SendsInline)
            {
                var cancellation = runtime.DurabilitySettings.Cancellation;
                var logger = runtime.LoggerFactory.CreateLogger<MqttSenderProtocol>();

                var durableTenanted = new CallbackAwareTenantedSender(Uri, Parent.TenantedIdBehavior,
                    new BatchedSender(this, new MqttSenderProtocol(this, Parent.Client), cancellation, logger));

                foreach (var tenant in Parent.Tenants)
                {
                    durableTenanted.RegisterSender(tenant.TenantId,
                        new BatchedSender(this, new MqttSenderProtocol(this, Parent.GetTenantClient(tenant)),
                            cancellation, logger));
                }

                return durableTenanted;
            }

            var defaultSender = new MqttTopicSender(this, Parent.Client);
            var tenantedSender = new TenantedSender(Uri, Parent.TenantedIdBehavior, defaultSender);

            foreach (var tenant in Parent.Tenants)
            {
                tenantedSender.RegisterSender(tenant.TenantId,
                    new MqttTopicSender(this, Parent.GetTenantClient(tenant)));
            }

            return tenantedSender;
        }

        // Inline keeps the historical immediate/fire-and-forget path (this itself, via SendAsync
        // below) so explicit inline usage is unaffected.
        if (SendsInline)
        {
            return this;
        }

        // Buffered and Durable both route through BatchedSender + MqttSenderProtocol so a send is
        // only considered successful once the broker has actually acknowledged it - see
        // MqttSenderProtocol for why that matters for UseDurableOutbox/UseDurableInbox backed by a
        // persistent message store (e.g. PersistMessagesWithSqlite). This mirrors the pattern used by
        // every other Wolverine transport with real broker acknowledgements (Pub/Sub, Rabbit, etc.).
        return new BatchedSender(this, new MqttSenderProtocol(this, Parent.Client),
            runtime.DurabilitySettings.Cancellation, runtime.LoggerFactory.CreateLogger<MqttSenderProtocol>());
    }

    bool ISender.SupportsNativeScheduledSend => false;
    Uri ISender.Destination => Uri;
    public async Task<bool> PingAsync()
    {
        try
        {
            await Parent.Client.PingAsync(_cancellation);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal ManagedMqttApplicationMessage BuildMessage(Envelope envelope)
    {
        var appMessage = new MqttApplicationMessage();
        EnvelopeMapper.MapEnvelopeToOutgoing(envelope, appMessage);

        var message = new ManagedMqttApplicationMessage
        {
            ApplicationMessage = appMessage,
            Id = Guid.NewGuid(),
        };

        return message;
    }

    public ValueTask SendAsync(Envelope envelope)
    {
        var message = BuildMessage(envelope);
        return new ValueTask(Parent.Client.EnqueueAsync(message));
    }

    public MqttQualityOfServiceLevel QualityOfServiceLevel { get; set; } = MqttQualityOfServiceLevel.AtLeastOnce;

    public bool Retain { get; set; } = false;
}