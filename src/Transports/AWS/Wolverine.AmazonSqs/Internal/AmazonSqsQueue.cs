using Amazon.SQS;
using Amazon.SQS.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Interop.MassTransit;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AmazonSqs.Internal;

public class AmazonSqsQueue : Endpoint, IBrokerQueue, IMassTransitInteropEndpoint
{
    /// <summary>
    ///     Hard Amazon SQS limit for the per-message DelaySeconds parameter (15 minutes). Scheduled
    ///     sends within this window to a standard queue are delayed natively by SQS; anything past it
    ///     falls back to Wolverine's own message scheduling
    /// </summary>
    public const int MaximumSqsDelaySeconds = 900;

    private readonly AmazonSqsTransport _parent;

    private bool _initialized;

    // This will vary later
    private int _visibilityTimeout = 120;

    internal Func<AmazonSqsQueue, IWolverineRuntime, ISqsEnvelopeMapper>? MapperFactory = null;

    internal AmazonSqsQueue(string queueName, AmazonSqsTransport parent) : base(
        new Uri($"{parent.Protocol}://{queueName}"),
        EndpointRole.Application)
    {
        _parent = parent;
        QueueName = queueName;
        EndpointName = queueName;
        BrokerRole = "queue";

        Configuration = new CreateQueueRequest(QueueName);

        MessageBatchSize = 10;
    }

    /// <summary>
    ///     Pluggable strategy for interoperability with non-Wolverine systems. Customizes how the incoming SQS requests
    ///     are read and how outgoing messages are written to SQS
    /// </summary>
    public ISqsEnvelopeMapper? Mapper { get; set; }

    // AmazonSqsQueue inherits raw Endpoint (not the typed Endpoint<,>), so the
    // generic base override doesn't apply. Surface "user wired their own SQS
    // mapper or factory" through the same protected hook so the
    // EndpointDescriptor reports InteropMode = "Custom" for SQS too. See #2641.
    protected internal override bool HasCustomEnvelopeMapper =>
        Mapper is not null || MapperFactory is not null;

    public string QueueName { get; }

    internal bool IsFifoQueue => QueueName.EndsWith(".fifo", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Opt this standard (non-FIFO) queue into Amazon SQS fair queues by mapping
    ///     <see cref="Envelope.GroupId"/> to the SQS <c>MessageGroupId</c> on outgoing messages.
    ///     This has no effect on FIFO queues, which always set <c>MessageGroupId</c>, and implies
    ///     no ordering or deduplication semantics. Default is <c>false</c>. See
    ///     https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/using-messagegroupid-property.html
    /// </summary>
    public bool EnableFairQueueMessageGroups { get; set; }

    // Set by the AmazonSqsTransport parent
    internal string? QueueUrl { get; private set; }

    /// <summary>
    ///     The duration (in seconds) that the received messages are hidden from subsequent retrieve
    ///     requests after being retrieved by a <code>ReceiveMessage</code> request. The default is
    ///     120.
    /// </summary>
    public int VisibilityTimeout
    {
        get => _visibilityTimeout;
        set
        {
            _visibilityTimeout = value;
            if (value > 0)
            {
                this.VisibilityTimeout(value);
            }
        }
    }

    /// <summary>
    ///     The duration (in seconds) for which the call waits for a message to arrive in the
    ///     queue before returning. If a message is available, the call returns sooner than <code>WaitTimeSeconds</code>.
    ///     If no messages are available and the wait time expires, the call returns successfully
    ///     with an empty list of messages. Default is 5.
    /// </summary>
    public int WaitTimeSeconds { get; set; } = 5;

    /// <summary>
    ///     The maximum number of messages to return. Amazon SQS never returns more messages than
    ///     this value (however, fewer messages might be returned). Valid values: 1 to 10. Default:
    ///     10.
    /// </summary>
    public int MaxNumberOfMessages { get; set; } = 10;

    /// <summary>
    ///     Additional configuration for how an SQS queue should be created
    /// </summary>
    [ChildDescription]
    public CreateQueueRequest Configuration { get; }

    private string? _deadLetterQueueName;
    private bool _deadLetterQueueNameSetExplicitly;

    /// <summary>
    ///     Name of the dead letter queue for this SQS queue where failed messages will be moved.
    ///     Resolution order:
    ///     <list type="number">
    ///       <item>If <c>ConfigureDeadLetterQueue</c> or <c>DisableDeadLetterQueueing</c> ran on
    ///       this listener, the explicit value (including <c>null</c> for "disabled") wins.</item>
    ///       <item>Otherwise, falls back to
    ///       <see cref="AmazonSqsTransport.DefaultDeadLetterQueueName"/> on the parent transport
    ///       — which itself defaults to <see cref="AmazonSqsTransport.DeadLetterQueueName"/>
    ///       (<c>"wolverine-dead-letter-queue"</c>) for hosts that haven't opted into a custom
    ///       transport-wide default.</item>
    ///     </list>
    ///     This means an unconfigured queue picks up whatever the transport's default is at the
    ///     point Wolverine reads the property — the order between
    ///     <c>UseAmazonSqsTransport().DefaultDeadLetterQueueName(...)</c> and the per-listener
    ///     bootstrap calls doesn't matter.
    /// </summary>
    public string? DeadLetterQueueName
    {
        get => _deadLetterQueueNameSetExplicitly
            ? _deadLetterQueueName
            : _parent.DefaultDeadLetterQueueName;
        set
        {
            _deadLetterQueueName = value;
            _deadLetterQueueNameSetExplicitly = true;
        }
    }

    /// <summary>
    ///     Optional list of message attribute names to request in ReceiveMessage.
    ///     Use "All" to retrieve all message attributes. If null or empty, nothing is requested.
    ///     (Attention: this is different from <see cref="ReceiveMessageRequest.MessageSystemAttributeNames"/>.)
    /// </summary>
    public List<string>? MessageAttributeNames { get; set; }

    public async ValueTask<bool> CheckAsync()
    {
        var response = await _parent.Client!.GetQueueUrlAsync(QueueName);
        return response.QueueUrl.IsNotEmpty();
    }

    public async ValueTask TeardownAsync(ILogger logger)
    {
        var client = _parent.Client!;

        if (QueueUrl.IsEmpty())
        {
            var response = await client.GetQueueUrlAsync(QueueName);
            QueueUrl = response.QueueUrl;
        }

        if (QueueUrl.IsEmpty())
        {
            return;
        }

        await client.DeleteQueueAsync(new DeleteQueueRequest(QueueUrl));
    }

    public ValueTask SetupAsync(ILogger logger)
    {
        return new ValueTask(SetupAsync(_parent.Client!));
    }

    public ValueTask PurgeAsync(ILogger logger)
    {
        return new ValueTask(PurgeAsync(_parent.Client!));
    }

    public async ValueTask<Dictionary<string, string>> GetAttributesAsync()
    {
        var client = _parent.Client!;

        if (QueueUrl.IsEmpty())
        {
            var response = await client.GetQueueUrlAsync(QueueName);
            QueueUrl = response.QueueUrl;
        }

        var atts = await client.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = QueueUrl
        });

        return new Dictionary<string, string>
        {
            { "name", QueueName },
            {
                nameof(GetQueueAttributesResponse.ApproximateNumberOfMessages),
                atts.ApproximateNumberOfMessages.ToString()
            },
            {
                nameof(GetQueueAttributesResponse.ApproximateNumberOfMessagesDelayed),
                atts.ApproximateNumberOfMessagesDelayed.ToString()
            },
            {
                nameof(GetQueueAttributesResponse.ApproximateNumberOfMessagesNotVisible),
                atts.ApproximateNumberOfMessagesNotVisible.ToString()
            }
        };
    }

    Uri? IMassTransitInteropEndpoint.MassTransitUri()
    {
        // amazonsqs://localhost/wolverine
        return new Uri($"amazonsqs://{_parent.ServerHost}/{QueueName}");
    }

    Uri? IMassTransitInteropEndpoint.MassTransitReplyUri()
    {
        var reply = _parent.ReplyEndpoint();
        return reply!.As<IMassTransitInteropEndpoint>().MassTransitUri();
    }

    Uri? IMassTransitInteropEndpoint.TranslateMassTransitToWolverineUri(Uri uri)
    {
        var lastSegment = uri.Segments.Last();
        return _parent.Queues[lastSegment].Uri;
    }

    internal ISqsEnvelopeMapper BuildMapper(IWolverineRuntime runtime)
    {
        if (Mapper != null)
        {
            return Mapper;
        }

        if (MapperFactory != null)
        {
            return MapperFactory(this, runtime);
        }

        return new DefaultSqsEnvelopeMapper();
    }

    /// <summary>
    ///     Can the requested delivery time of this envelope be honored natively by SQS through the
    ///     per-message DelaySeconds parameter? Standard queues only (FIFO queues support just a
    ///     queue-level delay), and only within the 15 minute SQS maximum
    /// </summary>
    internal bool CanScheduleNatively(Envelope envelope, DateTimeOffset utcNow)
    {
        if (IsFifoQueue)
        {
            return false;
        }

        if (envelope.ScheduledTime is not { } scheduledTime)
        {
            return true;
        }

        return scheduledTime.Subtract(utcNow).TotalSeconds <= MaximumSqsDelaySeconds;
    }

    /// <summary>
    ///     The DelaySeconds value to stamp on an outgoing SQS message for this envelope, or 0 for
    ///     "send immediately". Only applies to standard queues; SQS rejects per-message delays on
    ///     FIFO queues
    /// </summary>
    internal int NativeDelaySecondsFor(Envelope envelope, DateTimeOffset utcNow, ILogger logger)
    {
        if (IsFifoQueue || envelope.ScheduledTime is not { } scheduledTime)
        {
            return 0;
        }

        var remaining = scheduledTime.Subtract(utcNow);
        if (remaining <= TimeSpan.Zero)
        {
            return 0;
        }

        var seconds = (int)Math.Ceiling(remaining.TotalSeconds);
        if (seconds <= MaximumSqsDelaySeconds)
        {
            return seconds;
        }

        // Defensive only. Wolverine's routing falls back to its own message scheduling for
        // delays past the SQS maximum, so this should be unreachable through normal publishing
        logger.LogWarning(
            "Envelope {EnvelopeId} reached the SQS sender for queue {Queue} with a scheduled delay of {Seconds}s, which exceeds the SQS maximum of {MaximumSeconds}s. The message will be delivered after the maximum delay instead",
            envelope.Id, QueueName, seconds, MaximumSqsDelaySeconds);
        return MaximumSqsDelaySeconds;
    }

    internal async Task SendMessageAsync(Envelope envelope, ILogger logger)
    {
        if (!_initialized)
        {
            await InitializeAsync(logger);
        }

        Mapper ??= new DefaultSqsEnvelopeMapper();

        var body = Mapper!.BuildMessageBody(envelope);
        var request = new SendMessageRequest(QueueUrl, body);
        if (IsFifoQueue)
        {
            var groupId = Mapper.DetermineGroupId(envelope);
            if (groupId.IsNotEmpty())
            {
                request.MessageGroupId = groupId;
            }

            if (envelope.DeduplicationId.IsNotEmpty())
            {
                request.MessageDeduplicationId = envelope.DeduplicationId;
            }
        }
        else if (EnableFairQueueMessageGroups)
        {
            // SQS fair queues: a MessageGroupId on a standard queue improves tenant fairness.
            // No deduplication semantics apply to standard queues. See GH-2886.
            var groupId = Mapper.DetermineGroupId(envelope);
            if (groupId.IsNotEmpty())
            {
                request.MessageGroupId = groupId;
            }
        }

        foreach (var attribute in Mapper.ToAttributes(envelope))
        {
            request.MessageAttributes ??= new Dictionary<string, MessageAttributeValue>();
            request.MessageAttributes.Add(attribute.Key, attribute.Value);
        }

        var delaySeconds = NativeDelaySecondsFor(envelope, DateTimeOffset.UtcNow, logger);
        if (delaySeconds > 0)
        {
            request.DelaySeconds = delaySeconds;
        }

        await _parent.Client!.SendMessageAsync(request);
    }

    public override async ValueTask InitializeAsync(ILogger logger)
    {
        if (_initialized)
        {
            return;
        }

        var client = _parent.Client;

        if (client == null)
        {
            throw new InvalidOperationException($"Parent {nameof(AmazonSqsTransport)} has not been initialized");
        }

        try
        {
            if (_parent.AutoProvision)
            {
                await SetupAsync(client);
                logger.LogInformation("Tried to create Amazon SQS queue {Name} if missing", QueueUrl);
            }

            if (QueueUrl.IsEmpty())
            {
                var response = await client.GetQueueUrlAsync(QueueName);
                QueueUrl = response.QueueUrl;
            }

            if (_parent.AutoPurgeAllQueues)
            {
                await PurgeAsync(logger);
                logger.LogInformation("Purging Amazon SQS queue {Name}", QueueUrl);
            }
        }
        catch (Exception e)
        {
            throw new WolverineSqsTransportException($"Error while trying to initialize Amazon SQS queue '{QueueName}'",
                e);
        }

        _initialized = true;
    }

    internal async Task SetupAsync(IAmazonSQS client)
    {
        Configuration.QueueName = QueueName;
        try
        {
            var response = await client.CreateQueueAsync(Configuration);

            QueueUrl = response.QueueUrl;

            if (Role == EndpointRole.System)
            {
                await client.TagQueueAsync(new TagQueueRequest
                {
                    QueueUrl = QueueUrl,
                    Tags = new Dictionary<string, string>
                    {
                        ["wolverine:last-active"] = DateTime.UtcNow.ToString("o")
                    }
                });
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }

    public async Task PurgeAsync(IAmazonSQS client)
    {
        if (QueueUrl.IsEmpty())
        {
            var response = await client.GetQueueUrlAsync(QueueName);
            QueueUrl = response.QueueUrl;
        }

        try
        {
            await client.PurgeQueueAsync(QueueUrl);
        }
        catch (PurgeQueueInProgressException e)
        {
            Console.WriteLine(e.Message);
        }
    }

    public override async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        if (_parent.Client == null)
        {
            throw new InvalidOperationException("The parent transport has not yet been initialized");
        }

        Mapper ??= BuildMapper(runtime);

        var logger = runtime.LoggerFactory.CreateLogger<AmazonSqsQueue>();

        if (QueueUrl.IsEmpty())
        {
            await InitializeAsync(logger);
        }

        var listener = new SqsListener(runtime, this, _parent, receiver);

        // Broker-per-tenant (GH-3304): the shared listener consumes the default account. Each tenant runs its own
        // listener on its own account/region, stamping the tenant id onto inbound envelopes via TenantIdRule.
        // Per-envelope completion routes back over the receiving connection through Envelope.Listener — the same
        // CompoundListener multi-tenancy pattern used by RabbitMQ / NATS / Kafka.
        if (_parent.Tenants.Any() && TenancyBehavior == TenancyBehavior.TenantAware)
        {
            var compound = new CompoundListener(Uri);
            compound.Inner.Add(listener);

            foreach (var tenant in _parent.Tenants)
            {
                var tenantQueue = BuildTenantSibling(tenant);
                if (tenantQueue.QueueUrl.IsEmpty())
                {
                    await tenantQueue.InitializeAsync(logger);
                }

                var tenantReceiver = new ReceiverWithRules(receiver, [new TenantIdRule(tenant.TenantId)]);
                compound.Inner.Add(new SqsListener(runtime, tenantQueue, tenant.Transport, tenantReceiver));
            }

            return compound;
        }

        return listener;
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        Mapper ??= BuildMapper(runtime);

        // Broker-per-tenant (GH-3304): route by Envelope.TenantId to a per-tenant sender bound to that tenant's own
        // account, falling back to the shared account for the default/untenanted path.
        //
        // Both the tenant senders AND the default sender they fall back to must be simple fire-and-forget ISenders:
        // TenantedSender intentionally does NOT implement ISenderRequiresCallback (GH-2361), and it does not forward
        // RegisterCallback to the senders beneath it. A BatchedSender (SqsSenderProtocol) registered under it would
        // therefore never receive its ISenderCallback and would silently drop every message. InlineSqsSender sends
        // directly and needs no callback — the same fire-and-forget model the RabbitMQ / NATS / Kafka per-tenant
        // senders use.
        if (_parent.Tenants.Any() && TenancyBehavior == TenancyBehavior.TenantAware)
        {
            var tenantedSender = new TenantedSender(Uri, _parent.TenantedIdBehavior, new InlineSqsSender(runtime, this));
            foreach (var tenant in _parent.Tenants)
            {
                var tenantQueue = BuildTenantSibling(tenant);
                tenantedSender.RegisterSender(tenant.TenantId, new InlineSqsSender(runtime, tenantQueue));
            }

            return tenantedSender;
        }

        if (Mode == EndpointMode.Inline)
        {
            return new InlineSqsSender(runtime, this);
        }

        var protocol = new SqsSenderProtocol(runtime, this,
            _parent.Client ?? throw new InvalidOperationException("Parent transport has not been initialized"));
        var sender = new BatchedSender(this, protocol, runtime.Cancellation,
            runtime.LoggerFactory.CreateLogger<SqsSenderProtocol>());

        // FIFO queues only support a queue-level delay, never the per-message DelaySeconds, so
        // scheduled sends to a FIFO queue always fall back to Wolverine's own message scheduling
        if (IsFifoQueue)
        {
            sender.SupportsNativeScheduledSend = false;
        }

        return sender;
    }

    /// <summary>
    /// Broker-per-tenant (GH-3304): materialize this queue's tenant-specific twin on the given tenant's child
    /// transport — same queue name and configuration, but bound to the tenant's own SQS client and its own
    /// QueueUrl cache (which is why a fresh endpoint is required rather than reusing this one). The tenant twin is
    /// cached on the tenant transport's <see cref="AmazonSqsTransport.Queues"/> so repeated sender/listener builds
    /// resolve the same instance.
    /// </summary>
    internal AmazonSqsQueue BuildTenantSibling(AmazonSqsTenant tenant)
    {
        var sibling = tenant.Transport.Queues[QueueName];

        sibling.Mode = Mode;
        sibling.EndpointName = EndpointName;
        sibling.IsListener = IsListener;
        sibling.Role = Role;
        sibling.EnableFairQueueMessageGroups = EnableFairQueueMessageGroups;
        sibling.VisibilityTimeout = VisibilityTimeout;
        sibling.WaitTimeSeconds = WaitTimeSeconds;
        sibling.MaxNumberOfMessages = MaxNumberOfMessages;
        sibling.MessageAttributeNames = MessageAttributeNames;

        // Share the interop mapper strategy so tenant traffic serializes identically to the shared account.
        sibling.Mapper = Mapper;
        sibling.MapperFactory = MapperFactory;

        // Preserve queue-creation attributes (FIFO, retention, redrive, ...) for AutoProvision on the tenant account.
        if (Configuration.Attributes is { Count: > 0 })
        {
            sibling.Configuration.Attributes ??= new Dictionary<string, string>();
            foreach (var pair in Configuration.Attributes)
            {
                sibling.Configuration.Attributes[pair.Key] = pair.Value;
            }
        }

        // Only pin the dead letter queue name when it was set explicitly on this listener; otherwise let the tenant
        // queue fall back to the tenant transport's own DefaultDeadLetterQueueName (seeded in AmazonSqsTenant.Compile).
        if (_deadLetterQueueNameSetExplicitly)
        {
            sibling.DeadLetterQueueName = _deadLetterQueueName;
        }

        return sibling;
    }

    protected override bool supportsMode(EndpointMode mode)
    {
        return true;
    }

    internal void ConfigureRequest(ReceiveMessageRequest request)
    {
        request.WaitTimeSeconds = WaitTimeSeconds;
        request.MaxNumberOfMessages = MaxNumberOfMessages;
        request.VisibilityTimeout = VisibilityTimeout;

        if (MessageAttributeNames is { Count: > 0 })
        {
            request.MessageAttributeNames = MessageAttributeNames;
        }
    }

    public async Task TeardownAsync(IAmazonSQS client, CancellationToken token)
    {
        if (QueueUrl == null)
        {
            try
            {
                QueueUrl = (await client.GetQueueUrlAsync(QueueName, token)).QueueUrl;
            }
            catch (Exception)
            {
                return;
            }
        }

        await client.DeleteQueueAsync(new DeleteQueueRequest
        {
            QueueUrl = QueueUrl
        }, token);
    }

    internal void ConfigureDeadLetterQueue(Action<AmazonSqsQueue> configure)
    {
        if (DeadLetterQueueName != null)
        {
            var dlq = _parent.Queues[DeadLetterQueueName];
            configure(dlq);
        }
    }

    public override bool TryBuildDeadLetterSender(IWolverineRuntime runtime, out ISender? deadLetterSender)
    {
        if (DeadLetterQueueName.IsNotEmpty() && !_parent.DisableDeadLetterQueues)
        {
            var dlq = _parent.Queues[DeadLetterQueueName];
            deadLetterSender = new InlineSqsSender(runtime, dlq);
            return true;
        }

        deadLetterSender = default;
        return false;
    }

    public override DeadLetterStorageMode DeadLetterStorage =>
        DeadLetterQueueName.IsNotEmpty() && !_parent.DisableDeadLetterQueues
            ? DeadLetterStorageMode.Native
            : DeadLetterStorageMode.Durable;
}