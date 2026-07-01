using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
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
        : base(new Uri($"nats://subject/{subject}"), role)
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
    /// Transport-wide JetStream stream/consumer template applied when Wolverine auto-provisions.
    /// </summary>
    [IgnoreDescription]
    internal JetStreamDefaults JetStreamDefaults => _transport.Configuration.JetStreamDefaults;

    /// <summary>
    /// Normalize a per-message subject honoring the transport's
    /// <see cref="NatsTransportConfiguration.NormalizeSubjects"/> flag.
    /// </summary>
    internal string NormalizeSubject(string subject) => _transport.NormalizeSubjectIfEnabled(subject);
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
    public bool DeadLetterQueueEnabled { get; set; } = true;
    public string? DeadLetterSubject { get; set; }

    /// <summary>
    /// Per-endpoint override for the maximum delivery attempts / dead-letter threshold. When null the
    /// transport-wide <see cref="JetStreamDefaults.MaxDeliver"/> applies (see <see cref="EffectiveMaxDeliveryAttempts"/>).
    /// </summary>
    public int? MaxDeliveryAttempts { get; set; }

    /// <summary>
    /// Resolved maximum delivery attempts: the per-endpoint <see cref="MaxDeliveryAttempts"/> when set,
    /// otherwise the transport-wide <see cref="JetStreamDefaults.MaxDeliver"/>.
    /// </summary>
    [IgnoreDescription]
    internal int EffectiveMaxDeliveryAttempts => MaxDeliveryAttempts ?? JetStreamDefaults.MaxDeliver;

    /// <summary>
    /// Suffix appended to the destination subject to form the NATS JetStream scheduling subject for native
    /// scheduled sends. Must keep the schedule subject covered by the stream (e.g. a <c>prefix.&gt;</c>
    /// filter covers <c>{subject}.scheduled</c>). Defaults to <c>.scheduled</c>.
    /// </summary>
    public string ScheduleSubjectSuffix { get; set; } = ".scheduled";

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
    /// server default apply". Computed at access time so override mutations
    /// performed during host bootstrap are picked up regardless of ordering
    /// between transport / listener configuration calls.
    /// </summary>
    public ConsumerConfigDeliverPolicy? EffectiveDeliverPolicy =>
        DeliverPolicy ?? _transport.Configuration.JetStreamDefaults.DeliverPolicy;

    protected override bool supportsMode(EndpointMode mode)
    {
        return mode switch
        {
            EndpointMode.Inline => true,
            EndpointMode.BufferedInMemory => true,
            EndpointMode.Durable => UseJetStream,
            _ => false
        };
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        _connection = _transport.Connection;
        _logger = runtime.LoggerFactory.CreateLogger<NatsEndpoint>();
        _mapper = new NatsEnvelopeMapper(this);

        if (MessageType != null)
        {
            _mapper.ReceivesMessage(MessageType);
        }

        var useJetStream = UseJetStream && _transport.Configuration.EnableJetStream;
        var supportsScheduledSend = useJetStream && 
                                    _transport.ServerSupportsScheduledSend &&
                                    StreamName != null &&
                                    _transport.Configuration.Streams.TryGetValue(StreamName, out var streamConfig) &&
                                    streamConfig.AllowMsgSchedules;
        
        var jetStreamContext = useJetStream ? _transport.CreateJetStreamContext() : null;

        var baseSender = NatsSender.Create(
            this,
            _connection,
            jetStreamContext,
            _logger,
            _mapper,
            runtime.Cancellation,
            useJetStream,
            supportsScheduledSend
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
                    tenant.TenantId
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
        _connection = _transport.Connection;
        _logger = runtime.LoggerFactory.CreateLogger<NatsEndpoint>();

        ISender? deadLetterSender = null;
        if (!string.IsNullOrEmpty(DeadLetterSubject))
        {
            var dlqEndpoint = _transport.EndpointForSubject(DeadLetterSubject);
            deadLetterSender = (ISender)runtime.Endpoints.GetOrBuildSendingAgent(dlqEndpoint.Uri);
        }

        var useJetStream = UseJetStream && _transport.Configuration.EnableJetStream;

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
            runtime, receiver, _connection, useJetStream, subscriptionPattern, tenantMapper, deadLetterSender);

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
            var tenantReceiver = new ReceiverWithRules(receiver, [new TenantIdRule(tenant.TenantId)]);
            var tenantListener = await startListenerAsync(
                runtime, tenantReceiver, _transport.GetTenantConnection(tenant), useJetStream,
                subscriptionPattern, tenantMapper, deadLetterSender);
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
        ISender? deadLetterSender)
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
            tenantMapper
        );

        await listener.StartAsync();
        return listener;
    }

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

        try
        {
            var js = _transport.CreateJetStreamContext();
            var stream = await js.GetStreamAsync(
                StreamName,
                cancellationToken: CancellationToken.None
            );
            return stream != null;
        }
        catch
        {
            return false;
        }
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

        if (!UseJetStream || string.IsNullOrEmpty(StreamName))
        {
            logger.LogInformation("Using Core NATS for subject {Subject}", Subject);
            return;
        }

        var js = _connection.CreateJetStreamContext();

        try
        {
            var stream = await js.GetStreamAsync(StreamName);
            logger.LogInformation("Using existing JetStream stream {Stream}", StreamName);
        }
        catch
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

            logger.LogInformation(
                "Creating JetStream stream {Stream} for subjects {Subjects}",
                StreamName,
                string.Join(", ", subjects)
            );

            var defaults = JetStreamDefaults;
            var config = new StreamConfig(StreamName, subjects)
            {
                Retention = StreamConfigRetention.Workqueue,
                Discard = StreamConfigDiscard.Old,
                MaxAge = defaults.MaxAge ?? TimeSpan.Zero,
                MaxMsgs = defaults.MaxMessages ?? -1,
                MaxBytes = defaults.MaxBytes ?? -1,
                NumReplicas = defaults.Replicas,
                DuplicateWindow = defaults.DuplicateWindow
            };

            await js.CreateStreamAsync(config);
            logger.LogInformation("Created JetStream stream {Stream}", StreamName);
        }

        if (!string.IsNullOrEmpty(ConsumerName) && Role == EndpointRole.Application)
        {
            var consumerConfig = new ConsumerConfig
            {
                Name = ConsumerName,
                DurableName = ConsumerName,
                FilterSubject = Subject,
                AckPolicy = ConsumerConfigAckPolicy.Explicit,
                AckWait = JetStreamDefaults.AckWait,
                MaxDeliver = EffectiveMaxDeliveryAttempts,
                ReplayPolicy = ConsumerConfigReplayPolicy.Instant
            };

            await js.CreateOrUpdateConsumerAsync(StreamName, consumerConfig);
            logger.LogInformation(
                "Created/updated consumer {Consumer} on stream {Stream}",
                ConsumerName,
                StreamName
            );
        }
    }
}
