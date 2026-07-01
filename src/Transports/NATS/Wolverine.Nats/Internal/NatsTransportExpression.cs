using Wolverine.Nats.Configuration;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Nats.Internal;

public class NatsTransportExpression
    : BrokerExpression<
        NatsTransport,
        NatsEndpoint,
        NatsEndpoint,
        NatsListenerConfiguration,
        NatsSubscriberConfiguration,
        NatsTransportExpression
    >
{
    public NatsTransportExpression(NatsTransport transport, WolverineOptions options)
        : base(transport, options) { }

    /// <summary>
    /// Configure JetStream options
    /// </summary>
    public NatsTransportExpression UseJetStream(Action<JetStreamDefaults> configure)
    {
        configure(Transport.Configuration.JetStreamDefaults);
        Transport.Configuration.EnableJetStream = true;
        return this;
    }

    /// <summary>
    /// Project a domain identity into the JetStream deduplication key (<c>Nats-Msg-Id</c>) instead
    /// of the default Wolverine envelope Id, so non-Wolverine consumers get server-side dedup within
    /// the stream's duplicate window. Example: <c>e => $"{stream}/{version}"</c>. An explicit
    /// <c>Nats-Msg-Id</c> header already on the outgoing envelope still wins.
    /// </summary>
    public NatsTransportExpression DeduplicateUsing(Func<Envelope, string> msgIdSource)
    {
        Transport.Configuration.MsgIdSource = msgIdSource;
        return this;
    }

    /// <summary>
    /// Set the identifier prefix for all NATS subjects
    /// </summary>
    public NatsTransportExpression WithSubjectPrefix(string prefix)
    {
        Transport.IdentifierPrefix = prefix;
        return this;
    }

    /// <summary>
    /// Configure TLS settings
    /// </summary>
    public NatsTransportExpression UseTls(bool insecureSkipVerify = false)
    {
        Transport.Configuration.EnableTls = true;
        Transport.Configuration.TlsInsecure = insecureSkipVerify;
        return this;
    }

    /// <summary>
    /// Configure username/password authentication
    /// </summary>
    public NatsTransportExpression WithCredentials(string username, string password)
    {
        Transport.Configuration.Username = username;
        Transport.Configuration.Password = password;
        return this;
    }

    /// <summary>
    /// Configure token authentication
    /// </summary>
    public NatsTransportExpression WithToken(string token)
    {
        Transport.Configuration.Token = token;
        return this;
    }

    /// <summary>
    /// Configure NKey authentication
    /// </summary>
    public NatsTransportExpression WithNKey(string nkeyFile)
    {
        Transport.Configuration.NKeyFile = nkeyFile;
        return this;
    }

    /// <summary>
    /// Set the JetStream domain
    /// </summary>
    public NatsTransportExpression UseJetStreamDomain(string domain)
    {
        Transport.Configuration.JetStreamDomain = domain;
        return this;
    }

    /// <summary>
    /// Configure connection timeouts
    /// </summary>
    public NatsTransportExpression ConfigureTimeouts(
        TimeSpan connectTimeout,
        TimeSpan requestTimeout
    )
    {
        Transport.Configuration.ConnectTimeout = connectTimeout;
        Transport.Configuration.RequestTimeout = requestTimeout;
        return this;
    }

    /// <summary>
    /// Define a JetStream stream configuration
    /// </summary>
    public NatsTransportExpression DefineStream(
        string streamName,
        Action<StreamConfiguration> configure
    )
    {
        var streamConfig = new StreamConfiguration { Name = streamName };
        configure(streamConfig);
        Transport.Configuration.Streams[streamName] = streamConfig;
        Transport.Configuration.EnableJetStream = true;
        return this;
    }

    /// <summary>
    /// Define a work queue stream (retention by interest)
    /// </summary>
    public NatsTransportExpression DefineWorkQueueStream(
        string streamName,
        params string[] subjects
    )
    {
        return DefineWorkQueueStream(streamName, null, subjects);
    }

    /// <summary>
    /// Define a work queue stream (retention by interest) with additional configuration
    /// </summary>
    public NatsTransportExpression DefineWorkQueueStream(
        string streamName,
        Action<StreamConfiguration>? configure,
        params string[] subjects
    )
    {
        return DefineStream(
            streamName,
            stream =>
            {
                stream.AsWorkQueue().WithSubjects(subjects);
                configure?.Invoke(stream);
            }
        );
    }

    /// <summary>
    /// Define a log stream with time-based retention
    /// </summary>
    public NatsTransportExpression DefineLogStream(
        string streamName,
        TimeSpan retention,
        params string[] subjects
    )
    {
        return DefineStream(
            streamName,
            stream =>
            {
                stream.WithSubjects(subjects).WithLimits(maxAge: retention);
            }
        );
    }

    /// <summary>
    /// Define a high-availability stream with replication
    /// </summary>
    public NatsTransportExpression DefineReplicatedStream(
        string streamName,
        int replicas,
        params string[] subjects
    )
    {
        return DefineStream(
            streamName,
            stream =>
            {
                stream.WithSubjects(subjects).WithReplicas(replicas);
            }
        );
    }

    /// <summary>
    /// Configure multi-tenancy support
    /// </summary>
    public NatsTransportExpression ConfigureMultiTenancy(
        TenantedIdBehavior behavior = TenantedIdBehavior.FallbackToDefault
    )
    {
        Transport.TenantedIdBehavior = behavior;
        return this;
    }

    /// <summary>
    /// Set a custom tenant-subject mapper
    /// </summary>
    public NatsTransportExpression UseTenantSubjectMapper(ITenantSubjectMapper mapper)
    {
        Transport.TenantSubjectMapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        return this;
    }

    /// <summary>
    /// Add a tenant with subject-based isolation
    /// </summary>
    public NatsTransportExpression AddTenant(string tenantId)
    {
        var tenant = new NatsTenant(tenantId);
        Transport.Tenants[tenantId] = tenant;
        return this;
    }

    /// <summary>
    /// Add a tenant with a custom subject mapper
    /// </summary>
    public NatsTransportExpression AddTenant(string tenantId, ITenantSubjectMapper mapper)
    {
        var tenant = new NatsTenant(tenantId)
        {
            SubjectMapper = mapper
        };
        Transport.Tenants[tenantId] = tenant;
        return this;
    }

    /// <summary>
    /// Add a tenant that uses its own dedicated NATS connection, configured with the full connection / auth /
    /// TLS surface. The action receives a configuration seeded from the transport's own settings, so you only
    /// override what differs for this tenant — e.g. a different server or account, a token, JWT / NKey creds,
    /// a credentials file, or a client certificate.
    /// </summary>
    public NatsTransportExpression AddTenant(
        string tenantId,
        Action<NatsTransportConfiguration> configureConnection
    )
    {
        return AddTenant(tenantId, null, configureConnection);
    }

    /// <summary>
    /// Add a tenant with a dedicated connection (see
    /// <see cref="AddTenant(string,Action{NatsTransportConfiguration})"/>) and a custom subject mapper.
    /// </summary>
    public NatsTransportExpression AddTenant(
        string tenantId,
        ITenantSubjectMapper? mapper,
        Action<NatsTransportConfiguration> configureConnection
    )
    {
        ArgumentNullException.ThrowIfNull(configureConnection);

        var configuration = cloneConnectionConfiguration();
        configureConnection(configuration);

        Transport.Tenants[tenantId] = new NatsTenant(tenantId)
        {
            SubjectMapper = mapper,
            ConnectionConfiguration = configuration
        };
        return this;
    }

    // Seed a tenant connection configuration from the transport's own settings so a tenant only needs to
    // override what differs (mirrors RabbitMqTenant.Compile copying the parent's connection settings).
    private NatsTransportConfiguration cloneConnectionConfiguration()
    {
        var source = Transport.Configuration;

        var clone = new NatsTransportConfiguration();
        foreach (var property in typeof(NatsTransportConfiguration).GetProperties())
        {
            if (property is { CanRead: true, CanWrite: true })
            {
                property.SetValue(clone, property.GetValue(source));
            }
        }

        // The reflective copy above aliases the two mutable reference members by reference, so a tenant action
        // that mutates them in place (e.g. cfg.JetStreamDefaults.DuplicateWindow = ... or cfg.Streams[...] = ...)
        // would leak into the shared transport config and every other tenant. Give each tenant its own copy.
        clone.JetStreamDefaults = cloneJetStreamDefaults(source.JetStreamDefaults);
        clone.Streams = source.Streams.ToDictionary(pair => pair.Key, pair => cloneStream(pair.Value));

        return clone;
    }

    private static JetStreamDefaults cloneJetStreamDefaults(JetStreamDefaults source)
    {
        return new JetStreamDefaults
        {
            MaxAge = source.MaxAge,
            MaxMessages = source.MaxMessages,
            MaxBytes = source.MaxBytes,
            Replicas = source.Replicas,
            AckWait = source.AckWait,
            MaxDeliver = source.MaxDeliver,
            DuplicateWindow = source.DuplicateWindow,
            DeliverPolicy = source.DeliverPolicy
        };
    }

    private static StreamConfiguration cloneStream(StreamConfiguration source)
    {
        return new StreamConfiguration
        {
            Name = source.Name,
            Subjects = new List<string>(source.Subjects),
            Retention = source.Retention,
            Storage = source.Storage,
            MaxMessages = source.MaxMessages,
            MaxBytes = source.MaxBytes,
            MaxAge = source.MaxAge,
            MaxMessagesPerSubject = source.MaxMessagesPerSubject,
            DiscardPolicy = source.DiscardPolicy,
            Replicas = source.Replicas,
            AllowRollup = source.AllowRollup,
            AllowDirect = source.AllowDirect,
            DenyDelete = source.DenyDelete,
            DenyPurge = source.DenyPurge,
            DuplicateWindow = source.DuplicateWindow,
            AllowMsgSchedules = source.AllowMsgSchedules
        };
    }

    protected override NatsListenerConfiguration createListenerExpression(
        NatsEndpoint listenerEndpoint
    )
    {
        return new NatsListenerConfiguration(listenerEndpoint);
    }

    protected override NatsSubscriberConfiguration createSubscriberExpression(
        NatsEndpoint subscriberEndpoint
    )
    {
        return new NatsSubscriberConfiguration(subscriberEndpoint);
    }
}
