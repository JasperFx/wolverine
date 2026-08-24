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
    ///     The <c>MessageDeduplicationId</c> to send for this envelope, or null for none. A FIFO queue
    ///     without <c>ContentBasedDeduplication</c> rejects any send that carries no
    ///     <c>MessageDeduplicationId</c> at all, and Wolverine's own circuit-resume ping never has one --
    ///     so a latched sender could never probe its way back on such a queue. Fall back to the envelope
    ///     id for pings, which is unique per probe and is exactly the semantic we want (two pings must
    ///     never dedupe against each other, which content-based deduplication would happily do since every
    ///     ping body is identical). See GH-3793.
    /// </summary>
    internal static string? DetermineDeduplicationId(Envelope envelope)
    {
        if (envelope.DeduplicationId.IsNotEmpty())
        {
            return envelope.DeduplicationId;
        }

        return envelope.IsPing() ? envelope.Id.ToString() : null;
    }

    /// <summary>
    ///     Opt this standard (non-FIFO) queue into Amazon SQS fair queues by mapping
    ///     <see cref="Envelope.GroupId"/> to the SQS <c>MessageGroupId</c> on outgoing messages.
    ///     This has no effect on FIFO queues, which always set <c>MessageGroupId</c>, and implies
    ///     no ordering or deduplication semantics. Default is <c>false</c>. See
    ///     https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/using-messagegroupid-property.html
    /// </summary>
    public bool EnableFairQueueMessageGroups { get; set; }

    /// <summary>
    ///     Split an outgoing message that would exceed SQS's 256KB limit into several SQS messages, and
    ///     reassemble it on the receiving side. Default is <c>false</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     <b>Wolverine to Wolverine only.</b> The fragments are Wolverine's own framing carried in SQS
    ///     message attributes, so a non-Wolverine consumer of this queue sees N unintelligible messages
    ///     rather than one. That is why this is opt-in per endpoint rather than automatic.
    ///     </para>
    ///     <para>
    ///     <b>Reassembly happens in memory, per listener</b>, so every fragment of a message has to reach
    ///     the same listener. SQS is a competing-consumer queue, so use this only where that is
    ///     guaranteed:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>a <b>FIFO queue</b> — SQS delivers a message group to one consumer at a time, and
    ///         every fragment of a message shares a group id;</item>
    ///         <item>a listener using <b>GlobalPartitioning</b> — the fragments carry the message's
    ///         <see cref="Envelope.GroupId" />, so they are all routed to the node that owns that
    ///         group;</item>
    ///         <item>a <b>single listening node</b>.</item>
    ///     </list>
    ///     <para>
    ///     On a standard queue with several unpartitioned nodes the fragments scatter, no node completes
    ///     a set, and they are abandoned after <see cref="FragmentReassemblyTimeout" /> and redelivered.
    ///     Prefer a <a href="https://wolverinefx.net/guide/durability/claim-checks.html">claim check</a>
    ///     there.
    ///     </para>
    /// </remarks>
    public bool FragmentOversizedMessages { get; set; }

    /// <summary>
    ///     How long a listener holds an incomplete set of fragments before abandoning it. Default is 5
    ///     minutes. Abandoning forgets them locally; it does not delete them from SQS, so they become
    ///     visible again.
    /// </summary>
    public TimeSpan FragmentReassemblyTimeout { get; set; } = 5.Minutes();

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
    ///     Hard Amazon SQS limit on the number of entries in a single <c>DeleteMessageBatch</c>
    ///     request.
    /// </summary>
    public const int MaximumDeleteBatchSize = 10;

    private int _deleteMessageBatchSize = MaximumDeleteBatchSize;

    /// <summary>
    ///     How many message deletions this listener coalesces into a single
    ///     <c>DeleteMessageBatch</c> call. Completion is otherwise one HTTP round trip -- and one
    ///     billable API call -- per message, so a 10 message receive is paid for with 10 sequential
    ///     deletes. Valid values are 1 through 10; 1 reverts to a delete per message. Default 10.
    ///     See GH-3493.
    /// </summary>
    public int DeleteMessageBatchSize
    {
        get => _deleteMessageBatchSize;
        set
        {
            if (value < 1 || value > MaximumDeleteBatchSize)
            {
                throw new ArgumentOutOfRangeException(nameof(DeleteMessageBatchSize),
                    $"Must be between 1 and {MaximumDeleteBatchSize}");
            }

            _deleteMessageBatchSize = value;
        }
    }

    /// <summary>
    ///     The longest a completed message waits for its delete batch to fill before the batch is
    ///     sent anyway. This is a maximum batch age, not a quiet period. Default 50 milliseconds --
    ///     far inside any usable visibility timeout. See GH-3493.
    /// </summary>
    public TimeSpan DeleteMessageBatchTimeout { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    ///     Hard Amazon SQS limit on how long a single received message can be kept invisible, measured
    ///     from its receipt, across any number of <c>ChangeMessageVisibility</c> calls.
    /// </summary>
    public static readonly TimeSpan MaximumSqsVisibilityExtension = TimeSpan.FromHours(12);

    /// <summary>
    ///     GH-4019. For an <c>Inline</c> listener only: keep the messages of a received batch invisible
    ///     by extending their visibility timeout (<c>ChangeMessageVisibilityBatch</c>, every half
    ///     <see cref="VisibilityTimeout" />) until each is settled. Wolverine otherwise sets the
    ///     visibility once on receive and an inline listener handles the batch one message at a time,
    ///     so a batch whose handlers collectively outlive the timeout has its later messages redelivered
    ///     -- and executed again -- while they are still being handled. Costs nothing for a batch that
    ///     finishes inside half the timeout. Ignored for Buffered and Durable endpoints, which delete the
    ///     message before the handler runs. Default false.
    /// </summary>
    /// <remarks>
    ///     GH-4048: <b>not consulted for <c>NativeAck</c></b>, where renewal is unconditional. A NativeAck lane
    ///     holds the delivery for lane queue time plus handler time and queue time is unbounded by design, so an
    ///     un-renewed NativeAck endpoint is a duplicate-delivery generator by construction rather than merely at
    ///     risk under a slow handler -- an opt-in default-false flag would mean "off by default" for the one mode
    ///     that cannot survive it. <see cref="MaximumVisibilityExtension" /> remains the ceiling for both modes.
    /// </remarks>
    public bool ExtendVisibilityWhileHandling { get; set; }

    private TimeSpan _maximumVisibilityExtension = MaximumSqsVisibilityExtension;

    /// <summary>
    ///     The longest a single message is kept invisible from its receipt -- by
    ///     <see cref="ExtendVisibilityWhileHandling" /> under <c>Inline</c>, or unconditionally under
    ///     <c>NativeAck</c> -- before Wolverine stops extending it and lets SQS redeliver. Bounded above by the
    ///     SQS limit of 12 hours. Default 12 hours.
    /// </summary>
    public TimeSpan MaximumVisibilityExtension
    {
        get => _maximumVisibilityExtension;
        set
        {
            if (value <= TimeSpan.Zero || value > MaximumSqsVisibilityExtension)
            {
                throw new ArgumentOutOfRangeException(nameof(MaximumVisibilityExtension),
                    $"Must be greater than zero and no more than {MaximumSqsVisibilityExtension}");
            }

            _maximumVisibilityExtension = value;
        }
    }

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

        if (!SqsMessageFragments.ExceedsLimit(body))
        {
            await _parent.Client!.SendMessageAsync(buildSendRequest(envelope, body, null, logger));
            return;
        }

        // GH-3926: this is the one-at-a-time path (inline senders, requeues, dead letter forwarding), so
        // there is a caller to throw back at rather than a sender callback to report to. Either way an
        // oversized message can never be accepted by SQS, so failing loudly here beats letting SQS answer
        // with a SenderFault that a retry block would repeat forever.
        if (!FragmentOversizedMessages)
        {
            throw new SqsMessageTooLargeException(
                $"Envelope {envelope.Id} of message type {envelope.MessageType} produced a {body.Length} byte body for queue {QueueName}, over the {SqsMessageFragments.MaximumBodyBytes} bytes Wolverine will send in one SQS message (SQS caps a message and its attributes together at {SqsMessageFragments.MaximumMessageBytes}). Use a claim check (WolverineFx.ClaimCheck.AmazonS3), or opt this endpoint into FragmentOversizedMessages().");
        }

        var bodies = SqsMessageFragments.Split(body);

        if (bodies.Length > SqsMessageFragments.MaximumFragments)
        {
            throw new SqsMessageTooLargeException(
                $"Envelope {envelope.Id} of message type {envelope.MessageType} produced a {body.Length} byte body for queue {QueueName}, which would need {bodies.Length} fragments against a maximum of {SqsMessageFragments.MaximumFragments}. A message this large is a claim check problem rather than a framing one; see WolverineFx.ClaimCheck.AmazonS3.");
        }

        for (var i = 0; i < bodies.Length; i++)
        {
            var header = new SqsFragmentHeader(envelope.Id, i, bodies.Length);
            await _parent.Client!.SendMessageAsync(buildSendRequest(envelope, bodies[i], header, logger));
        }
    }

    private SendMessageRequest buildSendRequest(Envelope envelope, string body, SqsFragmentHeader? fragment,
        ILogger logger)
    {
        var request = new SendMessageRequest(QueueUrl, body);

        if (IsFifoQueue)
        {
            var groupId = groupIdFor(envelope, fragment);
            if (groupId.IsNotEmpty())
            {
                request.MessageGroupId = groupId;
            }

            var deduplicationId = DetermineDeduplicationId(envelope);
            if (deduplicationId.IsNotEmpty())
            {
                // Every fragment of one envelope would otherwise carry the identical deduplication id,
                // and a FIFO queue would keep exactly one of them.
                request.MessageDeduplicationId = fragment is { } header
                    ? $"{deduplicationId}-{header.Index}"
                    : deduplicationId;
            }
        }
        else if (EnableFairQueueMessageGroups)
        {
            // SQS fair queues: a MessageGroupId on a standard queue improves tenant fairness.
            // No deduplication semantics apply to standard queues. See GH-2886.
            var groupId = groupIdFor(envelope, fragment);
            if (groupId.IsNotEmpty())
            {
                request.MessageGroupId = groupId;
            }
        }

        foreach (var attribute in Mapper!.ToAttributes(envelope))
        {
            request.MessageAttributes ??= new Dictionary<string, MessageAttributeValue>();
            request.MessageAttributes.Add(attribute.Key, attribute.Value);
        }

        if (fragment is { } h)
        {
            request.MessageAttributes ??= new Dictionary<string, MessageAttributeValue>();
            foreach (var pair in SqsMessageFragments.AttributesFor(h.FragmentId, h.Index, h.Count))
            {
                request.MessageAttributes[pair.Key] = pair.Value;
            }
        }

        var delaySeconds = NativeDelaySecondsFor(envelope, DateTimeOffset.UtcNow, logger);
        if (delaySeconds > 0)
        {
            request.DelaySeconds = delaySeconds;
        }

        return request;
    }

    private string? groupIdFor(Envelope envelope, SqsFragmentHeader? fragment)
    {
        return fragment is { } header
            ? SqsMessageFragments.GroupIdFor(envelope, header.FragmentId)
            : Mapper!.DetermineGroupId(envelope);
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

        if (SendsInline)
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
        sibling.FragmentOversizedMessages = FragmentOversizedMessages;
        sibling.FragmentReassemblyTimeout = FragmentReassemblyTimeout;
        sibling.DeleteMessageBatchSize = DeleteMessageBatchSize;
        sibling.DeleteMessageBatchTimeout = DeleteMessageBatchTimeout;
        sibling.ExtendVisibilityWhileHandling = ExtendVisibilityWhileHandling;
        sibling.MaximumVisibilityExtension = MaximumVisibilityExtension;

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

    /// <summary>
    /// GH-4048. An unsettled SQS delivery is invisible only for <see cref="VisibilityTimeout" /> seconds, after
    /// which SQS redelivers it -- so a NativeAck endpoint here has to renew that clock for as long as the
    /// envelope sits in an execution lane. <see cref="SqsListener" /> supplies the renewal through
    /// <see cref="ISupportLeaseRenewal" />.
    /// </summary>
    protected internal override bool holdsExpiringLease => true;

    internal void ConfigureRequest(ReceiveMessageRequest request)
    {
        request.WaitTimeSeconds = WaitTimeSeconds;
        request.MaxNumberOfMessages = MaxNumberOfMessages;
        request.VisibilityTimeout = VisibilityTimeout;

        request.MessageAttributeNames = _receivedAttributeNames ??= resolveAttributeNames();
    }

    private List<string>? _receivedAttributeNames;

    /// <summary>
    ///     Whatever the endpoint asked for, plus Wolverine's own fragment framing (GH-3926). SQS returns
    ///     only the attributes a receive names, so leaving the framing off makes a fragmented message
    ///     arrive as N unrelated messages that each fail to deserialize -- and a listener has to be able
    ///     to read fragments whether or not this same endpoint is configured to send them.
    ///     Resolved once and cached, since endpoint configuration is fixed by the time anything polls.
    /// </summary>
    private List<string> resolveAttributeNames()
    {
        var names = MessageAttributeNames is { Count: > 0 }
            ? new List<string>(MessageAttributeNames)
            : [];

        // "All" already covers the framing
        if (names.Contains("All"))
        {
            return names;
        }

        foreach (var name in SqsMessageFragments.AttributeNames)
        {
            if (!names.Contains(name))
            {
                names.Add(name);
            }
        }

        return names;
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