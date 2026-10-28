using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Wolverine.Configuration;
using Wolverine.Nats.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Nats.Internal;

public class NatsEndpoint : Endpoint, IBrokerEndpoint
{
    private readonly NatsTransport _transport;
    private NatsConnection? _connection;
    private ILogger<NatsEndpoint>? _logger;
    private NatsEnvelopeMapper? _mapper;

    public NatsEndpoint(string subject, NatsTransport transport, EndpointRole role)
        : base(new Uri($"{transport.Protocol}://subject/{subject}"), role)
    {
        Subject = subject;
        _transport = transport;

        EndpointName = subject;
        Mode = EndpointMode.BufferedInMemory;
    }

    /// <summary>
    /// NATS plays both roles: Core NATS surfaces a "subject", while JetStream surfaces
    /// a "stream". The choice is configuration-driven (<see cref="UseJetStream"/>) so
    /// it can change after construction — compute it on access rather than fixing it
    /// in the constructor. See GH-2601.
    /// </summary>
    public override string BrokerRole => UseJetStream ? "stream" : "subject";

    public string Subject { get; }
    [IgnoreDescription]
    public object? NatsSerializer { get; set; }
    public Dictionary<string, string> CustomHeaders { get; set; } = new();

    /// <summary>
    /// Optional transport-wide hook to rewrite the outgoing subject per envelope
    /// (headers, tenant, aggregate id) beyond what per-message topic routing can express.
    /// Sourced from <see cref="NatsTransportConfiguration.SubjectResolver"/>.
    /// </summary>
    [IgnoreDescription]
    internal ISubjectResolver? SubjectResolver => _transport.Configuration.SubjectResolver;

    /// <summary>
    /// Optional transport-wide source of the JetStream <c>Nats-Msg-Id</c> dedup key.
    /// Sourced from <see cref="NatsTransportConfiguration.MsgIdSource"/>.
    /// </summary>
    [IgnoreDescription]
    internal Func<Envelope, string>? MsgIdSource => _transport.Configuration.MsgIdSource;

    /// <summary>
    /// Optional transport-wide last word over JetStream publish options (GH-4860).
    /// Sourced from <see cref="NatsTransportConfiguration.ConfigureJetStreamPublish"/>.
    /// </summary>
    [IgnoreDescription]
    internal Func<Envelope, NatsJSPubOpts, NatsJSPubOpts>? ConfigureJetStreamPublish =>
        _transport.Configuration.ConfigureJetStreamPublish;

    /// <summary>
    /// Transport-wide JetStream stream/consumer template applied when Wolverine auto-provisions.
    /// </summary>
    [IgnoreDescription]
    internal JetStreamDefaults JetStreamDefaults => _transport.Configuration.JetStreamDefaults;

    /// <summary>
    /// What the listener does with its named JetStream consumer when that already exists.
    /// Sourced from <see cref="NatsTransportConfiguration.Provisioning"/>.
    /// </summary>
    [IgnoreDescription]
    internal NatsProvisioning ConsumerProvisioning => _transport.Configuration.ConsumerProvisioning;

    /// <summary>
    /// See <see cref="NatsTransportConfiguration.TrustTenantIdHeader"/>
    /// </summary>
    [IgnoreDescription]
    internal bool TrustsTenantIdHeader => _transport.Configuration.TrustTenantIdHeader;

    /// <summary>
    /// The ids of the tenants configured on the transport
    /// </summary>
    internal HashSet<string> TenantIds() => _transport.Tenants.Select(x => x.TenantId).ToHashSet();

    /// <summary>
    /// The name of the transport this endpoint belongs to: <c>nats</c>, or the name of an additional broker
    /// </summary>
    internal string TransportName => _transport.Protocol;

    /// <summary>
    /// Normalize a per-message subject honoring the transport's
    /// <see cref="NatsTransportConfiguration.NormalizeSubjects"/> flag.
    /// </summary>
    internal string NormalizeSubject(string subject) => _transport.NormalizeSubjectIfEnabled(subject);

    /// <summary>
    /// The reply subject a request sent through this endpoint carries on the wire; see
    /// <see cref="NatsTransport.WireReplySubjectFor"/>.
    /// </summary>
    internal string WireReplySubjectFor(Envelope envelope) => _transport.WireReplySubjectFor(envelope);

    /// <summary>
    /// See <see cref="NatsTransport.RememberWireReplySubject"/>.
    /// </summary>
    internal void RememberWireReplySubject(Envelope request, string? wireReplySubject) =>
        _transport.RememberWireReplySubject(request, wireReplySubject);

    /// <summary>
    /// See <see cref="NatsTransport.ReplySubjectFor"/>.
    /// </summary>
    internal string ReplySubjectFor(Envelope envelope, string targetSubject) =>
        _transport.ReplySubjectFor(envelope, targetSubject);

    /// <summary>
    /// See <see cref="NatsTransport.ForgetWireReplySubject"/>.
    /// </summary>
    internal void ForgetWireReplySubject(Envelope envelope) => _transport.ForgetWireReplySubject(envelope);

    public string? QueueGroup { get; set; }

    /// <summary>
    /// The queue group actually used for load-balanced delivery: the per-endpoint
    /// <see cref="QueueGroup"/> when set, otherwise the transport-wide
    /// <see cref="NatsTransportConfiguration.DefaultQueueGroup"/>.
    /// </summary>
    [IgnoreDescription]
    internal string? EffectiveQueueGroup =>
        string.IsNullOrEmpty(QueueGroup) ? _transport.Configuration.DefaultQueueGroup : QueueGroup;

    public string? StreamName { get; set; }
    public string? ConsumerName { get; set; }
    public bool UseJetStream { get; set; }

    /// <summary>
    ///     GH-4026. For a Durable JetStream listener: the most consumed messages the subscriber coalesces
    ///     (for at most 5ms) into one batched inbox insert, instead of one insert round trip per message --
    ///     the same micro-batching the RabbitMQ and Kafka listeners have. 1 reverts to strict
    ///     message-at-a-time persistence. Ignored for Buffered/Inline endpoints and for core NATS.
    ///     Default 100.
    /// </summary>
    public int MaximumMessagesToReceive { get; set; } = 100;
    public bool DeadLetterQueueEnabled { get; set; } = true;
    public string? DeadLetterSubject { get; set; }

    /// <summary>
    /// Per-endpoint override for the JetStream consumer's maximum delivery attempts (<c>MaxDeliver</c>). When null
    /// the transport-wide <see cref="JetStreamDefaults.MaxDeliver"/> applies (see <see cref="EffectiveMaxDeliveryAttempts"/>).
    /// </summary>
    public int? MaxDeliveryAttempts { get; set; }

    /// <summary>
    /// Resolved maximum delivery attempts: the per-endpoint <see cref="MaxDeliveryAttempts"/> when set,
    /// otherwise the transport-wide <see cref="JetStreamDefaults.MaxDeliver"/>.
    /// </summary>
    [IgnoreDescription]
    internal int EffectiveMaxDeliveryAttempts => MaxDeliveryAttempts ?? JetStreamDefaults.MaxDeliver;

    /// <summary>
    /// GH-4053. Per-endpoint override for the JetStream consumer's <c>AckWait</c> -- how long the server will
    /// wait for an unacknowledged delivery before redelivering it. When null the transport-wide
    /// <see cref="Configuration.JetStreamDefaults.AckWait"/> applies. This is the clock
    /// <see cref="EndpointMode.NativeAck"/> renews through <c>ISupportLeaseRenewal</c>.
    /// </summary>
    public TimeSpan? AckWait { get; set; }

    /// <summary>
    /// Resolved <c>AckWait</c> for this endpoint: per-endpoint <see cref="AckWait"/> wins over the
    /// transport-wide <see cref="Configuration.JetStreamDefaults.AckWait"/>.
    /// </summary>
    [IgnoreDescription]
    internal TimeSpan EffectiveAckWait => AckWait ?? JetStreamDefaults.AckWait;

    private TimeSpan _maximumAckExtension = TimeSpan.FromHours(12);

    /// <summary>
    /// GH-4053. The longest a single JetStream delivery is kept alive by <c>AckProgress</c> renewals, measured
    /// from its receipt, before Wolverine stops renewing and lets the server redeliver. Unlike SQS there is no
    /// server-side cap on JetStream renewal, so this exists purely as Wolverine's own ceiling on how long one
    /// unsettled delivery may pin a consumer's <see cref="MaxAckPending"/> slot. Default 12 hours, matching the
    /// SQS ceiling. Only consulted under <see cref="EndpointMode.NativeAck"/>.
    /// </summary>
    public TimeSpan MaximumAckExtension
    {
        get => _maximumAckExtension;
        set
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(MaximumAckExtension), "Must be greater than zero");
            }

            _maximumAckExtension = value;
        }
    }

    /// <summary>
    /// GH-4053. Explicit override for the JetStream consumer's <c>MaxAckPending</c> -- the number of deliveries
    /// the server will leave unacknowledged before it stops delivering. Null leaves the NATS server default
    /// (1,000) in place for every mode except <see cref="EndpointMode.NativeAck"/>, which computes its own
    /// value. See <see cref="EffectiveMaxAckPending"/>.
    /// </summary>
    public int? MaxAckPending { get; set; }

    /// <summary>
    /// GH-4053. Resolved <c>MaxAckPending</c>, or null to leave the server default alone.
    /// </summary>
    /// <remarks>
    /// <c>MaxAckPending</c> is JetStream's prefetch equivalent, and under <see cref="EndpointMode.NativeAck"/>
    /// it IS the back pressure: nothing is acked until the handler succeeds, so the unacked window is what
    /// bounds the in-memory execution block. It therefore has to cover every lane that can be busy at once --
    /// the partition slot count when the endpoint is group-partitioned and <see cref="Endpoint.MaxDegreeOfParallelism"/>
    /// otherwise, doubled so a lane is never starved waiting on the next delivery. Sized under that, the consumer
    /// stalls itself: the server stops delivering while lanes sit idle. This mirrors
    /// <c>RabbitMqQueue.PreFetchCount</c>'s NativeAck arm exactly.
    /// </remarks>
    [IgnoreDescription]
    internal int? EffectiveMaxAckPending
    {
        get
        {
            if (MaxAckPending.HasValue)
            {
                return MaxAckPending.Value;
            }

            if (Mode != EndpointMode.NativeAck)
            {
                // Every other mode settles on receipt (Buffered/Durable) or one at a time (Inline), so the
                // server default has never been the binding constraint. Don't change what they see.
                return null;
            }

            var lanes = GroupShardingSlotNumber.HasValue
                ? Math.Max((int)GroupShardingSlotNumber.Value, MaxDegreeOfParallelism)
                : MaxDegreeOfParallelism;

            return lanes * 2;
        }
    }

    /// <summary>
    /// Suffix appended to the destination subject to form the NATS JetStream scheduling subjects for native
    /// scheduled sends: each scheduled message gets its own <c>{subject}{suffix}.{envelope id}</c>. The stream
    /// has to cover those subjects, e.g. with a <c>prefix.&gt;</c> subject, or <c>{subject}.scheduled.&gt;</c>
    /// next to an exact subject. Defaults to <c>.scheduled</c>.
    /// </summary>
    public string ScheduleSubjectSuffix { get; set; } = ".scheduled";

    /// <summary>
    /// True when a send through this endpoint can hand a scheduled message to the server: JetStream, a server
    /// with message schedules, and a declared stream that allows them and keeps them until they fire. Interest
    /// and work-queue retention cannot promise that: an interest stream drops a control message no consumer is
    /// interested in, and on both a consumer whose filter covers the schedule subject deletes the control message
    /// by acknowledging it, so the schedule never fires. Such sends use Wolverine's own scheduling instead.
    /// </summary>
    [IgnoreDescription]
    internal bool SupportsNativeScheduledSend =>
        UseJetStream
        && _transport.Configuration.EnableJetStream
        && _transport.ServerSupportsScheduledSend
        && StreamName != null
        && _transport.Configuration.Streams.TryGetValue(StreamName, out var scheduleStreamConfig)
        && scheduleStreamConfig.AllowMsgSchedules
        && scheduleStreamConfig.Retention == StreamConfigRetention.Limits
        && capturesScheduleSubjects(scheduleStreamConfig);

    // Each schedule has a subject of its own under the suffix. A stream declared with the exact subjects of the single
    // schedule subject used before captures none of them, and every native schedule would be published to nowhere
    private bool capturesScheduleSubjects(StreamConfiguration stream) =>
        stream.Subjects.Any(x => SubjectCovers(x, $"{Subject}{ScheduleSubjectSuffix}.{Guid.Empty:N}"));

    /// <summary>
    /// Per-endpoint override for the JetStream consumer's <c>DeliverPolicy</c>.
    /// When non-null this wins over
    /// <see cref="Configuration.JetStreamDefaults.DeliverPolicy"/>; when null the
    /// transport-wide default applies, and when both are null Wolverine leaves
    /// <c>DeliverPolicy</c> unset on the auto-provisioned <c>ConsumerConfig</c>
    /// — falling through to the NATS server default of
    /// <see cref="ConsumerConfigDeliverPolicy.All"/>. See
    /// <see cref="EffectiveDeliverPolicy"/> for the resolved value.
    /// </summary>
    public ConsumerConfigDeliverPolicy? DeliverPolicy { get; set; }

    /// <summary>
    /// Resolved <c>DeliverPolicy</c> for this endpoint: per-endpoint
    /// <see cref="DeliverPolicy"/> wins over the transport-wide
    /// <see cref="Configuration.JetStreamDefaults.DeliverPolicy"/>, with
    /// <c>null</c> meaning "leave the consumer config alone and let the NATS
    /// server default apply". A shard listener on the work-queue stream
    /// <c>UseShardedNatsSubjects()</c> declares delivers all instead of the
    /// transport-wide default, the only policy a work-queue stream accepts.
    /// Computed at access time so override mutations performed during host
    /// bootstrap are picked up regardless of ordering between transport /
    /// listener configuration calls.
    /// </summary>
    public ConsumerConfigDeliverPolicy? EffectiveDeliverPolicy =>
        DeliverPolicy ?? (consumesShardStream
            ? ConsumerConfigDeliverPolicy.All
            : _transport.Configuration.JetStreamDefaults.DeliverPolicy);

    private bool consumesShardStream =>
        StreamName != null &&
        _transport.Configuration.Streams.TryGetValue(StreamName, out var stream) &&
        stream.DeclaredByShardTopology;

    /// <summary>
    /// The subject pattern this endpoint's listener consumes: the plain <see cref="Subject"/>, or the tenant
    /// wildcard pattern when the endpoint is tenant-aware under subject isolation. Resource setup and the
    /// listener both derive the consumer filter from it.
    /// </summary>
    [IgnoreDescription]
    internal string ListenerSubscriptionPattern =>
        _transport.Tenants.Any() && TenancyBehavior == TenancyBehavior.TenantAware
            ? _transport.TenantSubjectMapper.GetSubscriptionPattern(Subject)
            : Subject;

    /// <summary>
    /// The settings Wolverine owns on this endpoint's JetStream consumers: explicit acks, <c>AckWait</c>,
    /// <c>MaxDeliver</c>, the subject filter and, where Wolverine sizes it, <c>MaxAckPending</c>. One place for
    /// the listener, which creates consumers and reconciles or verifies a named one under
    /// <see cref="NatsProvisioning"/>, and for resource setup, so the two cannot drift apart. The filter is part
    /// of that set on purpose: when resource setup and the listener disagreed on it, every start rewrote the
    /// consumer and a <see cref="NatsProvisioning.Verify"/> host could never come up after resource setup.
    /// </summary>
    /// <param name="subscriptionPattern">
    /// The subject pattern the consumer filters on, normally <see cref="ListenerSubscriptionPattern"/>
    /// </param>
    internal ConsumerConfig ApplyManagedConsumerSettings(ConsumerConfig config, string subscriptionPattern)
    {
        config.AckPolicy = ConsumerConfigAckPolicy.Explicit;
        config.AckWait = EffectiveAckWait;
        config.MaxDeliver = EffectiveMaxDeliveryAttempts;

        // GH-4053: MaxAckPending is JetStream's prefetch equivalent, and under NativeAck it is the whole of the
        // back pressure -- nothing is acked until a handler succeeds, so the unacked window is what bounds the
        // in-memory execution block. Sized under the number of lanes that can be busy at once, the consumer
        // stalls itself; see EffectiveMaxAckPending. Null for every other mode, which leaves the NATS server
        // default of 1,000 exactly where it was.
        if (EffectiveMaxAckPending is { } maxAckPending)
        {
            config.MaxAckPending = maxAckPending;
        }

        // Scope the consumer to this listener's subject. Without a filter every durable consumer sharing a
        // stream received every message published to that stream (GH-3676). Native scheduling's control
        // messages stay outside the filter on purpose: the server materializes a scheduled message onto this
        // subject at its due time, and a consumer that also took the control message handled it at once --
        // and the materialized copy a second time.
        if (!string.IsNullOrEmpty(subscriptionPattern))
        {
            config.FilterSubject = subscriptionPattern;
            config.FilterSubjects = null;
        }

        return config;
    }

    protected override bool supportsMode(EndpointMode mode)
    {
        return mode switch
        {
            EndpointMode.Inline => true,
            EndpointMode.BufferedInMemory => true,
            EndpointMode.Durable => UseJetStream,

            // GH-4053. A deliberate arm rather than a leak: this switch's `_ => false` correctly refused
            // NativeAck before this issue, so the mode had to be admitted explicitly. JetStream-only, and the
            // JetStream half of that decision is made in validateModeConfiguration() below rather than here.
            EndpointMode.NativeAck => true,
            _ => false
        };
    }

    /// <summary>
    /// GH-4053. JetStream settles each delivery individually (<c>Ack</c> / <c>Nak</c> / <c>AckTerminate</c> off
    /// <see cref="NatsEnvelope.JetStreamMsg"/>) and does not care what order the acks arrive in, so out-of-order
    /// settlement from a worker thread is expressible -- which is what <see cref="EndpointMode.NativeAck"/> requires.
    /// </summary>
    /// <remarks>
    /// Answering the JetStream question HERE, in the <see cref="Endpoint.Mode"/> setter's predicate, would make the
    /// answer depend on the order the user happened to write two fluent calls: <c>UseJetStream()</c> and
    /// <c>ProcessInParallelWithNativeAcks()</c> are both applied as delayed configuration, so
    /// <c>.ProcessInParallelWithNativeAcks().UseJetStream("X")</c> would be refused while
    /// <c>.UseJetStream("X").ProcessInParallelWithNativeAcks()</c> was accepted -- for identical configurations.
    /// GH-4047's <see cref="validateModeConfiguration"/> exists for exactly this shape of question and runs over
    /// the FINAL state after <see cref="Endpoint.Compile"/>, so core NATS is refused deterministically and with a
    /// message that says why. Azure Service Bus resolved the same ordering problem the same way for
    /// <c>RequireSessions()</c> in GH-4049.
    /// </remarks>
    protected override bool supportsNativeAck => true;

    /// <summary>
    /// GH-4053. Native acks are JetStream-only. Core NATS has no redelivery-on-unacked semantics at all -- a core
    /// subscription is fire-and-forget, there is nothing to leave unsettled and nothing to hand back -- so the
    /// crash-safety story the mode exists to provide does not exist there. A core NATS "native ack" endpoint would
    /// be BufferedInMemory with extra steps and a false no-loss promise, which is worse than refusing it.
    /// </summary>
    protected internal override IEnumerable<string> validateModeConfiguration()
    {
        if (Mode != EndpointMode.NativeAck) yield break;

        if (!UseJetStream)
        {
            yield return
                $"Invalid listener configuration for endpoint '{Uri}': ProcessInParallelWithNativeAcks() requires JetStream. " +
                "Core NATS delivers a message once and forgets it -- there is no unacknowledged delivery to hold and no " +
                "redelivery when a node dies mid-flight -- so EndpointMode.NativeAck cannot deliver the no-loss guarantee " +
                "it promises. Add UseJetStream(...) to this listener, or use BufferedInMemory()/ProcessInline() on core NATS.";

            yield break;
        }

        if (!_transport.Configuration.EnableJetStream)
        {
            yield return
                $"Invalid listener configuration for endpoint '{Uri}': ProcessInParallelWithNativeAcks() requires JetStream, " +
                "but JetStream has been disabled transport-wide, so this listener falls back to core NATS and would settle " +
                "nothing. Re-enable JetStream on the transport, or drop ProcessInParallelWithNativeAcks() from this listener.";
        }
    }

    /// <summary>
    /// GH-4048/GH-4053. A JetStream delivery is only unacknowledged for <see cref="EffectiveAckWait"/> before the
    /// server redelivers it, so a NativeAck endpoint here has to renew that clock for as long as the envelope sits
    /// in an execution lane. <see cref="NatsListener"/> supplies the renewal through <c>ISupportLeaseRenewal</c>,
    /// and <c>ListeningAgent</c> refuses to start a NativeAck listener that does not.
    ///
    /// <para>
    /// False for core NATS, where an un-acked message simply does not exist as a concept -- there is no clock and
    /// nothing to renew. That combination is refused outright by <see cref="validateModeConfiguration"/> anyway;
    /// this keeps the two answers consistent rather than asserting a lease core NATS does not have.
    /// </para>
    /// </summary>
    protected internal override bool holdsExpiringLease => UseJetStream;

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        _logger = runtime.LoggerFactory.CreateLogger<NatsEndpoint>();

        var useJetStream = UseJetStream && _transport.Configuration.EnableJetStream;
        var supportsScheduledSend = SupportsNativeScheduledSend;

        if (!supportsScheduledSend && useJetStream && _transport.ServerSupportsScheduledSend &&
            StreamName != null &&
            _transport.Configuration.Streams.TryGetValue(StreamName, out var streamConfig) &&
            streamConfig.AllowMsgSchedules)
        {
            if (streamConfig.Retention != StreamConfigRetention.Limits)
            {
                _logger.LogInformation(
                    "Stream {Stream} allows message schedules, but its {Retention} retention can delete a schedule before it fires, so messages scheduled for {Subject} use Wolverine's own scheduling",
                    StreamName, streamConfig.Retention, Subject);
            }
            else
            {
                _logger.LogWarning(
                    "Stream {Stream} allows message schedules, but its subjects do not capture {ScheduleSubjects}, where each message scheduled for {Subject} gets a schedule subject of its own, so they use Wolverine's own scheduling. Add the subject to the stream to schedule them natively",
                    StreamName, $"{Subject}{ScheduleSubjectSuffix}.>", Subject);
            }
        }

        return buildSender(runtime, useJetStream, supportsScheduledSend);
    }

    /// <summary>
    /// The sender a JetStream listener forwards its dead letters through when this endpoint is its dead-letter
    /// subject: a JetStream publish whose PubAck is checked, whatever this endpoint is configured for otherwise,
    /// so the listener knows the copy is stored before it lets go of the original. Built here rather than taken
    /// from the subject's sending agent: an agent buffers (or would hand back an agent rather than an
    /// <see cref="ISender"/>, GH-3739), and the subject's own endpoint may well be core NATS. Tenant routing is the
    /// same as for any send to this subject; <see cref="NatsTransportConfiguration.ConfigureJetStreamPublish"/> is
    /// not applied to the copy.
    /// </summary>
    internal ISender BuildDeadLetterSender(IWolverineRuntime runtime)
    {
        Compile(runtime);
        _logger ??= runtime.LoggerFactory.CreateLogger<NatsEndpoint>();
        return buildSender(runtime, useJetStream: true, supportsScheduledSend: false, deadLetters: true);
    }

    private ISender buildSender(IWolverineRuntime runtime, bool useJetStream, bool supportsScheduledSend,
        bool deadLetters = false)
    {
        _connection = _transport.Connection;
        _logger ??= runtime.LoggerFactory.CreateLogger<NatsEndpoint>();
        _mapper = new NatsEnvelopeMapper(this);

        if (MessageType != null)
        {
            _mapper.ReceivesMessage(MessageType);
        }

        var jetStreamContext = useJetStream ? _transport.CreateJetStreamContext() : null;

        var baseSender = NatsSender.Create(
            this,
            _connection,
            jetStreamContext,
            _logger,
            _mapper,
            runtime.Cancellation,
            useJetStream,
            supportsScheduledSend,
            // The application's publish hook shapes its own publishes; on a dead-letter copy an expected stream or a
            // fixed Nats-Msg-Id it sets would make the dead-letter stream refuse or drop every copy
            applyPublishHook: !deadLetters
        );

        if (_transport.Tenants.Any() && TenancyBehavior == TenancyBehavior.TenantAware)
        {
            var tenantedSender = new TenantedSender(Uri, _transport.TenantedIdBehavior, baseSender);

            foreach (var tenant in _transport.Tenants)
            {
                var subjectMapper = tenant.SubjectMapper ?? _transport.TenantSubjectMapper;
                var tenantSubject = subjectMapper.MapSubject(Subject, tenant.TenantId);

                // Tenants that declare their own connection/credentials publish over their dedicated
                // connection (with its own JetStream context); the rest reuse the shared connection.
                var tenantConnection = _transport.GetTenantConnection(tenant);
                var tenantJetStreamContext =
                    useJetStream ? _transport.CreateJetStreamContext(tenantConnection) : null;

                var tenantEndpoint = new NatsEndpoint(tenantSubject, _transport, Role)
                {
                    UseJetStream = UseJetStream,
                    StreamName = StreamName,
                    ConsumerName = ConsumerName,
                    QueueGroup = QueueGroup,
                    DeadLetterQueueEnabled = DeadLetterQueueEnabled,
                    DeadLetterSubject = DeadLetterSubject,
                    MaxDeliveryAttempts = MaxDeliveryAttempts,
                    DeliverPolicy = DeliverPolicy,
                    MessageType = MessageType,
                    CustomHeaders = CustomHeaders,
                    NatsSerializer = NatsSerializer
                };

                var tenantSender = NatsSender.Create(
                    tenantEndpoint,
                    tenantConnection,
                    tenantJetStreamContext,
                    _logger,
                    _mapper,
                    runtime.Cancellation,
                    useJetStream,
                    supportsScheduledSend,
                    subjectMapper,
                    tenant.TenantId,
                    applyPublishHook: !deadLetters
                );

                tenantedSender.RegisterSender(tenant.TenantId, tenantSender);
            }

            return tenantedSender;
        }

        return baseSender;
    }

    public override async ValueTask<IListener> BuildListenerAsync(
        IWolverineRuntime runtime,
        IReceiver receiver
    )
    {
        // A listener the agent framework starts after the host -- an exclusive listener, a shard -- fails without
        // failing the host, so the transport's health check has to report it
        try
        {
            var listener = await buildListenerAsync(runtime, receiver);
            _transport.ListenerStarted(Uri);
            return listener;
        }
        catch (Exception e)
        {
            _transport.ListenerFailedToStart(Uri, e);
            throw;
        }
    }

    private async ValueTask<IListener> buildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        _connection = _transport.Connection;
        _logger = runtime.LoggerFactory.CreateLogger<NatsEndpoint>();

        var useJetStream = UseJetStream && _transport.Configuration.EnableJetStream;

        // Only a JetStream delivery can be dead lettered: a core NATS message is gone once it is delivered
        ISender? deadLetterSender = null;
        if (useJetStream && !string.IsNullOrEmpty(DeadLetterSubject))
        {
            deadLetterSender = _transport.EndpointForSubject(DeadLetterSubject).BuildDeadLetterSender(runtime);
        }

        string subscriptionPattern = Subject;
        ITenantSubjectMapper? tenantMapper = null;
        var tenantAware = _transport.Tenants.Any() && TenancyBehavior == TenancyBehavior.TenantAware;

        if (tenantAware)
        {
            tenantMapper = _transport.TenantSubjectMapper;
            subscriptionPattern = tenantMapper.GetSubscriptionPattern(Subject);
        }

        // The shared listener consumes the default connection plus every subject-prefix tenant, whose messages
        // arrive on the shared connection under the wildcard pattern.
        var sharedListener = await startListenerAsync(
            runtime, receiver, _connection, useJetStream, subscriptionPattern, tenantMapper, deadLetterSender, null);

        // Tenants with their own connection publish on a separate server/account the shared listener can't see,
        // so consume each over its own connection and stamp the tenant id onto inbound envelopes. Mirrors the
        // RabbitMQ / Azure Service Bus CompoundListener multi-tenancy pattern; per-envelope completion routes
        // back to the right connection via Envelope.Listener.
        var dedicatedTenants = tenantAware
            ? _transport.Tenants.Where(t => t.HasOwnConnection).ToArray()
            : [];

        if (dedicatedTenants.Length == 0)
        {
            return sharedListener;
        }

        var compound = new CompoundListener(Uri);
        compound.Inner.Add(sharedListener);

        foreach (var tenant in dedicatedTenants)
        {
            // The tenant's own mapper, as for sending to it: with SameSubjectTenantMapper the tenant's producers
            // publish to the plain subject, which a JetStream consumer filtering on the transport's "*.{subject}"
            // never took
            var mapper = tenant.SubjectMapper ?? tenantMapper!;
            var tenantReceiver = new ReceiverWithRules(receiver, [new TenantIdRule(tenant.TenantId)]);

            NatsListener tenantListener;
            try
            {
                tenantListener = await startListenerAsync(
                    runtime, tenantReceiver, _transport.GetTenantConnection(tenant), useJetStream,
                    mapper.GetSubscriptionPattern(Subject), mapper, deadLetterSender, tenant.TenantId);
            }
            catch
            {
                // The listeners already running would keep consuming for a listening agent that saw the start fail
                try
                {
                    await compound.DisposeAsync();
                }
                catch (Exception e)
                {
                    // The start failure is what the listening agent and the health check have to see
                    _logger?.LogDebug(e, "Error disposing the listeners of {Uri} after a failed start", Uri);
                }

                throw;
            }

            compound.Inner.Add(tenantListener);
        }

        return compound;
    }

    private async ValueTask<NatsListener> startListenerAsync(
        IWolverineRuntime runtime,
        IReceiver receiver,
        NatsConnection connection,
        bool useJetStream,
        string subscriptionPattern,
        ITenantSubjectMapper? tenantMapper,
        ISender? deadLetterSender,
        string? connectionTenantId)
    {
        var jetStreamContext = useJetStream ? _transport.CreateJetStreamContext(connection) : null;

        var listener = NatsListener.Create(
            this,
            connection,
            jetStreamContext,
            runtime,
            receiver,
            _logger!,
            deadLetterSender,
            runtime.Cancellation,
            useJetStream,
            subscriptionPattern,
            tenantMapper,
            connectionTenantId
        );

        try
        {
            await listener.StartAsync();
        }
        catch
        {
            // Nothing else owns it yet
            await listener.DisposeAsync();
            throw;
        }

        return listener;
    }

    /// <summary>
    /// Startup, right after the transport connected: the declared streams have to be in place before any listener
    /// starts. Resource checks and the other resource operations never get here, so they provision nothing.
    /// </summary>
    public override async ValueTask InitializeAsync(ILogger logger)
    {
        await _transport.ProvisionDeclaredStreamsAsync(explicitSetup: false);
    }

    /// <summary>
    /// <c>resources check</c>: whether this endpoint's stream exists, and -- read-only -- whether a declared one
    /// matches its declaration where startup would act on a difference. A missing stream is reported as missing;
    /// a differing one throws with the differences, which the resource reports with the endpoint.
    /// </summary>
    public async ValueTask<bool> CheckAsync()
    {
        _connection ??= _transport.Connection;

        if (_connection == null || _connection.ConnectionState != NatsConnectionState.Open)
        {
            return false;
        }

        if (!UseJetStream || string.IsNullOrEmpty(StreamName))
        {
            return true;
        }

        StreamConfig existing;
        try
        {
            var js = _transport.CreateJetStreamContext();
            existing = (await js.GetStreamAsync(StreamName, cancellationToken: CancellationToken.None)).Info.Config;
        }
        catch (NatsJSApiException e) when (e.Error.Code == 404)
        {
            // Only "stream not found" means missing. Any other failure -- no JetStream answering for the domain,
            // missing permissions, a timeout -- propagates, so the check reports it instead of a missing stream
            return false;
        }

        var deviations = _transport.DeclaredStreamDeviations(StreamName, existing);
        if (deviations.Count > 0)
        {
            throw new InvalidOperationException(
                $"JetStream stream '{StreamName}' does not match its declaration ({_transport.Configuration.StreamProvisioning}): " +
                string.Join("; ", deviations));
        }

        return true;
    }

    public async ValueTask TeardownAsync(ILogger logger)
    {
        _connection ??= _transport.Connection;

        if (_connection == null || _connection.ConnectionState != NatsConnectionState.Open)
        {
            return;
        }

        if (!UseJetStream || string.IsNullOrEmpty(StreamName))
        {
            return;
        }

        if (!string.IsNullOrEmpty(ConsumerName))
        {
            try
            {
                var js = _transport.CreateJetStreamContext();
                await js.DeleteConsumerAsync(StreamName, ConsumerName);
                logger.LogInformation(
                    "Deleted consumer {Consumer} from stream {Stream}",
                    ConsumerName,
                    StreamName
                );
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to delete consumer {Consumer} from stream {Stream}",
                    ConsumerName,
                    StreamName
                );
            }
        }
    }

    public async ValueTask SetupAsync(ILogger logger)
    {
        _connection ??= _transport.Connection;

        if (_connection == null || _connection.ConnectionState != NatsConnectionState.Open)
        {
            throw new InvalidOperationException("NATS connection is not available or not open");
        }

        // The declared streams first, from their declarations and as Provisioning says -- also those no endpoint
        // uses, and with Verify failing the setup. Once per resource setup, whichever endpoint gets here first
        await _transport.ProvisionDeclaredStreamsAsync(explicitSetup: true);

        if (!UseJetStream || string.IsNullOrEmpty(StreamName))
        {
            logger.LogInformation("Using Core NATS for subject {Subject}", Subject);
            return;
        }

        // The transport's factory, like every other JetStream call here, so a configured JetStream domain or
        // API prefix applies to the stream lookup and creation and to the consumer update below
        var js = _transport.CreateJetStreamContext(_connection);

        try
        {
            await js.GetStreamAsync(StreamName);
            logger.LogInformation("Using existing JetStream stream {Stream}", StreamName);
        }
        catch (NatsJSApiException e) when (e.Error.Code == 404)
        {
            // Only a missing stream is created. Any other failure -- no JetStream answering for the domain,
            // missing permissions, a timeout -- propagates instead of being taken for "not found"
            var subjects = StreamSubjects();

            StreamConfig config;
            if (_transport.Configuration.Streams.TryGetValue(StreamName, out var declared))
            {
                // GH-4956. The application declared this stream through DefineStream() and friends, so
                // resource setup creates THAT stream -- the same configuration start-up provisioning builds --
                // rather than a default-shaped work-queue stream the declaration then never gets to correct
                // under CreateOnly, and that Verify reports as drift against the declaration. The endpoint's
                // own subjects join it only if the declaration does not already cover them, so a declaration
                // with a wildcard stays byte-for-byte what was declared.
                config = JetStreamProvisioning.BuildStreamConfig(StreamName, declared, _transport.Configuration.JetStreamDefaults);

                var declaredSubjects = (config.Subjects ?? []).ToList();
                foreach (var subject in subjects.Where(s => !declaredSubjects.Any(d => SubjectCovers(d, s))))
                {
                    declaredSubjects.Add(subject);
                }

                config.Subjects = declaredSubjects;

                logger.LogInformation(
                    "Creating the declared JetStream stream {Stream} for subjects {Subjects}",
                    StreamName,
                    string.Join(", ", declaredSubjects)
                );
            }
            else
            {
                logger.LogInformation(
                    "Creating JetStream stream {Stream} for subjects {Subjects}",
                    StreamName,
                    string.Join(", ", subjects)
                );

                var defaults = JetStreamDefaults;
                config = new StreamConfig(StreamName, subjects)
                {
                    Retention = StreamConfigRetention.Workqueue,
                    Discard = StreamConfigDiscard.Old,
                    MaxAge = defaults.MaxAge ?? TimeSpan.Zero,
                    MaxMsgs = defaults.MaxMessages ?? -1,
                    MaxBytes = defaults.MaxBytes ?? -1,
                    NumReplicas = defaults.Replicas,
                    DuplicateWindow = defaults.DuplicateWindow
                };
            }

            await js.CreateStreamAsync(config);
            logger.LogInformation("Created JetStream stream {Stream}", StreamName);
        }

        if (!string.IsNullOrEmpty(ConsumerName) && Role == EndpointRole.Application)
        {
            // The same filter the listener puts on the consumer, so a start after resource setup finds nothing
            // to reconcile or to fail Verify on
            var consumerConfig = ApplyManagedConsumerSettings(new ConsumerConfig
            {
                Name = ConsumerName,
                DurableName = ConsumerName,
                ReplayPolicy = ConsumerConfigReplayPolicy.Instant
            }, ListenerSubscriptionPattern);

            await js.CreateOrUpdateConsumerAsync(StreamName, consumerConfig);
            logger.LogInformation(
                "Created/updated consumer {Consumer} on stream {Stream}",
                ConsumerName,
                StreamName
            );
        }
    }

    /// <summary>
    /// GH-4956. Whether a NATS subject pattern (with <c>*</c> for one token and <c>&gt;</c> for the rest) covers a
    /// concrete subject, the way the server matches a stream's subjects
    /// </summary>
    /// <summary>
    /// The subjects a stream this endpoint uses has to capture: its own, and with tenants the tenant pattern
    /// </summary>
    internal List<string> StreamSubjects()
    {
        var subjects = new List<string> { Subject };

        if (_transport.Tenants.Any() && TenancyBehavior == TenancyBehavior.TenantAware)
        {
            var wildcardPattern = _transport.TenantSubjectMapper.GetSubscriptionPattern(Subject);
            if (wildcardPattern != Subject)
            {
                subjects.Add(wildcardPattern);
            }
        }

        return subjects;
    }

    internal static bool SubjectCovers(string pattern, string subject)
    {
        if (pattern == subject)
        {
            return true;
        }

        var patternTokens = pattern.Split('.');
        var subjectTokens = subject.Split('.');

        for (var i = 0; i < patternTokens.Length; i++)
        {
            var token = patternTokens[i];
            if (token == ">")
            {
                return subjectTokens.Length > i;
            }

            if (i >= subjectTokens.Length)
            {
                return false;
            }

            if (token != "*" && token != subjectTokens[i])
            {
                return false;
            }
        }

        return patternTokens.Length == subjectTokens.Length;
    }
}
