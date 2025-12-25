using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Wolverine.Configuration;
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

    public string Subject { get; }
    public object? NatsSerializer { get; set; }
    public Dictionary<string, string> CustomHeaders { get; set; } = new();
    public string? QueueGroup { get; set; }
    public string? StreamName { get; set; }
    public string? ConsumerName { get; set; }
    public bool UseJetStream { get; set; }
    public bool DeadLetterQueueEnabled { get; set; } = true;
    public string? DeadLetterSubject { get; set; }
    public int MaxDeliveryAttempts { get; set; } = 5;

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

        var baseSender = NatsSender.Create(
            this,
            _connection,
            _logger,
            _mapper,
            runtime.Cancellation,
            UseJetStream && _transport.Configuration.EnableJetStream
        );

        if (_transport.Tenants.Any() && TenancyBehavior == TenancyBehavior.TenantAware)
        {
            var tenantedSender = new TenantedSender(Uri, _transport.TenantedIdBehavior, baseSender);

            foreach (var tenant in _transport.Tenants)
            {
                var subjectMapper = tenant.SubjectMapper ?? _transport.TenantSubjectMapper;
                var tenantSubject = subjectMapper.MapSubject(Subject, tenant.TenantId);

                var tenantEndpoint = new NatsEndpoint(tenantSubject, _transport, Role)
                {
                    UseJetStream = UseJetStream,
                    StreamName = StreamName,
                    ConsumerName = ConsumerName,
                    QueueGroup = QueueGroup,
                    DeadLetterQueueEnabled = DeadLetterQueueEnabled,
                    DeadLetterSubject = DeadLetterSubject,
                    MaxDeliveryAttempts = MaxDeliveryAttempts,
                    MessageType = MessageType,
                    CustomHeaders = CustomHeaders,
                    NatsSerializer = NatsSerializer
                };

                var tenantSender = NatsSender.Create(
                    tenantEndpoint,
                    _connection,
                    _logger,
                    _mapper,
                    runtime.Cancellation,
                    UseJetStream && _transport.Configuration.EnableJetStream
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

        string subscriptionPattern = Subject;
        ITenantSubjectMapper? tenantMapper = null;

        if (_transport.Tenants.Any() && TenancyBehavior == TenancyBehavior.TenantAware)
        {
            tenantMapper = _transport.TenantSubjectMapper;
            subscriptionPattern = tenantMapper.GetSubscriptionPattern(Subject);
        }

        var listener = NatsListener.Create(
            this,
            _connection,
            runtime,
            receiver,
            _logger,
            deadLetterSender,
            runtime.Cancellation,
            UseJetStream && _transport.Configuration.EnableJetStream,
            subscriptionPattern,
            tenantMapper
        );

        await listener.StartAsync();

        return listener;
    }

    public NatsHeaders BuildHeaders(Envelope envelope)
    {
        var headers = new NatsHeaders();
        _mapper?.MapEnvelopeToOutgoing(envelope, headers);

        foreach (var header in CustomHeaders)
        {
            headers[header.Key] = header.Value;
        }

        return headers;
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
            var js = _connection.CreateJetStreamContext();
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
                var js = _connection.CreateJetStreamContext();
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

            var config = new StreamConfig(StreamName, subjects)
            {
                Retention = StreamConfigRetention.Workqueue,
                Discard = StreamConfigDiscard.Old,
                MaxAge = TimeSpan.FromDays(1),
                DuplicateWindow = TimeSpan.FromMinutes(2),
                MaxMsgs = 1_000_000
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
                AckWait = TimeSpan.FromSeconds(30),
                MaxDeliver = MaxDeliveryAttempts,
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
