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
        return DefineStream(
            streamName,
            stream =>
            {
                stream.AsWorkQueue().WithSubjects(subjects);
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
    /// Add a tenant with custom credentials
    /// </summary>
    public NatsTransportExpression AddTenantWithCredentials(
        string tenantId,
        string username,
        string password
    )
    {
        var tenant = new NatsTenant(tenantId)
        {
            Username = username,
            Password = password
        };
        Transport.Tenants[tenantId] = tenant;
        return this;
    }

    /// <summary>
    /// Add a tenant with JWT credentials file
    /// </summary>
    public NatsTransportExpression AddTenantWithCredentialsFile(
        string tenantId,
        string credentialsFile
    )
    {
        var tenant = new NatsTenant(tenantId)
        {
            CredentialsFile = credentialsFile
        };
        Transport.Tenants[tenantId] = tenant;
        return this;
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
