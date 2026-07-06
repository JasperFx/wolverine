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

namespace Wolverine.Nats.Internal;

public class NatsTransport : BrokerTransport<NatsEndpoint>, IAsyncDisposable
{
    public const string ProtocolName = "nats";

    private readonly JasperFx.Core.LightweightCache<string, NatsEndpoint> _endpoints = new();
    private NatsConnection? _connection;
    private INatsJSContext? _jetStreamContext;
    private ILogger<NatsTransport>? _logger;
    
    /// <summary>
    /// Minimum NATS server version required for scheduled message delivery
    /// </summary>
    private static readonly Version MinScheduledSendVersion = new(2, 12, 0);
    
    /// <summary>
    /// Whether the connected NATS server supports scheduled message delivery (v2.12+)
    /// </summary>
    public bool ServerSupportsScheduledSend { get; private set; }

    internal JasperFx.Core.LightweightCache<string, NatsTenant> Tenants { get; } = new();
    internal ITenantSubjectMapper TenantSubjectMapper { get; set; } = new DefaultTenantSubjectMapper();

    public NatsTransport() : this(ProtocolName)
    {
    }

    /// <summary>
    /// Constructor used when connecting to more than one NATS broker from a single application. The
    /// <paramref name="protocol"/> doubles as the additional broker's URI scheme so its endpoints don't
    /// collide with the default <c>nats://</c> broker. Reached through
    /// <see cref="TransportCollection.GetOrCreate{T}"/> when a <see cref="BrokerName"/> is supplied.
    /// </summary>
    public NatsTransport(string protocol)
        : base(protocol, "NATS Transport", ["nats.io"])
    {
        _endpoints.OnMissing = subject =>
        {
            var normalized = NormalizeSubjectIfEnabled(subject);
            return new NatsEndpoint(normalized, this, EndpointRole.Application);
        };
    }

    // GH-3269: built straight from the connection string, which may embed userinfo (nats://user:pass@host). Suppressed
    // from the reflected diagnostic tree so credentials never leak; the sanitized target is on DescribeEndpoint().
    [IgnoreDescription]
    public override Uri ResourceUri =>
        Configuration.ConnectionString != null
            ? new Uri(Configuration.ConnectionString)
            : new Uri("nats://localhost:4222");

    public string ResponseSubject { get; private set; } = "wolverine.response";

    public override string? DescribeEndpoint()
    {
        var cs = Configuration.ConnectionString;
        if (string.IsNullOrWhiteSpace(cs)) return null;

        // The connection string may be a comma-separated server list and may embed userinfo (nats://user:pass@host);
        // report host:port only so no credentials are surfaced.
        var servers = cs
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(safeHostPort)
            .Where(x => x != null);

        var summary = string.Join(", ", servers);
        return string.IsNullOrEmpty(summary) ? null : summary;
    }

    private static string? safeHostPort(string server)
    {
        if (Uri.TryCreate(server, UriKind.Absolute, out var uri))
        {
            var port = uri.Port > 0 ? uri.Port : 4222;
            return $"{uri.Host}:{port}";
        }

        return null;
    }

    [ChildDescription]
    public NatsTransportConfiguration Configuration { get; } = new();

    // Live runtime objects (not configuration) that throw before the transport connects — never part of the
    // diagnostic description.
    [IgnoreDescription]
    public NatsConnection Connection =>
        _connection ?? throw new InvalidOperationException("NATS connection not initialized");

    [IgnoreDescription]
    public INatsJSContext JetStreamContext =>
        _jetStreamContext
        ?? throw new InvalidOperationException("JetStream context not initialized");

    protected override IEnumerable<NatsEndpoint> endpoints() => _endpoints;

    protected override NatsEndpoint findEndpointByUri(Uri uri)
    {
        var subject = ExtractSubjectFromUri(uri);
        return _endpoints[subject];
    }

    public override Endpoint ReplyEndpoint()
    {
        return _endpoints[ResponseSubject];
    }

    public override async ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
        _logger = runtime.LoggerFactory.CreateLogger<NatsTransport>();

        // The per-node reply subject must be unique to this running node. In Solo mode the
        // assigned node number is always 1 (#3188), so several Solo services on one broker would
        // collide on the same subject and cross-deliver each other's replies — use the always
        // unique UniqueNodeId instead. Balanced nodes get a unique AssignedNodeNumber via election,
        // so they keep the existing, more readable subject. See #3189.
        var responseNode = runtime.Options.Durability.Mode == DurabilityMode.Solo
            ? runtime.Options.UniqueNodeId.ToString("N")
            : runtime.Options.Durability.AssignedNodeNumber.ToString();
        ResponseSubject = $"wolverine.response.{responseNode}";
        var responseEndpoint = _endpoints[ResponseSubject];
        responseEndpoint.IsUsedForReplies = true;
        responseEndpoint.IsListener = true;

        var natsOpts = Configuration.ToNatsOpts();
        natsOpts = natsOpts with { Name = $"wolverine-{runtime.Options.ServiceName}" };
        _connection = new NatsConnection(natsOpts);
        await _connection.ConnectAsync();

        _logger.LogInformation("Connected to NATS at {Url}", Configuration.ConnectionString);
        
        // Check server version for scheduled send support
        if (_connection.ServerInfo?.Version != null && 
            Version.TryParse(_connection.ServerInfo.Version.Split('-')[0], out var serverVersion))
        {
            ServerSupportsScheduledSend = serverVersion >= MinScheduledSendVersion;
            if (ServerSupportsScheduledSend)
            {
                _logger.LogInformation(
                    "NATS server version {Version} supports scheduled message delivery",
                    _connection.ServerInfo.Version);
            }
        }

        var autoProvisionStreams = Configuration.AutoProvision && Configuration.Streams.Any();

        if (Configuration.EnableJetStream)
        {
            _jetStreamContext = CreateJetStreamContext();
            _logger.LogInformation("JetStream context initialized");

            if (autoProvisionStreams)
            {
                await ProvisionStreamsAsync(_jetStreamContext);
            }
        }

        // Tenants that declare their own connection string / credentials get a dedicated connection they
        // own for the lifetime of the transport; the NATS client connects lazily on first use. Tenants
        // without their own connection reuse the shared connection above (subject-prefix isolation only).
        foreach (var tenant in Tenants.Where(x => x.HasOwnConnection))
        {
            var tenantConnection = new NatsConnection(buildTenantNatsOpts(tenant));
            tenant.Connection = tenantConnection;
            _logger.LogInformation("Created dedicated NATS connection for tenant {TenantId}", tenant.TenantId);

            // Each tenant server is its own JetStream instance, so mirror the configured streams onto it
            // (the streams the shared connection just provisioned don't exist on the tenant's server).
            if (Configuration.EnableJetStream && autoProvisionStreams)
            {
                await ProvisionStreamsAsync(CreateJetStreamContext(tenantConnection));
            }
        }
    }

    public WolverineTransportHealthCheck BuildHealthCheck(IWolverineRuntime runtime)
    {
        return new NatsHealthCheck(this);
    }

    public override IEnumerable<PropertyColumn> DiagnosticColumns()
    {
        yield return new PropertyColumn("Subject", "header");
        yield return new PropertyColumn("Queue Group", "header");
        yield return new PropertyColumn("JetStream", "header");
        yield return new PropertyColumn("Consumer Name");
    }

    public static string NormalizeSubject(string subject)
    {
        return subject.Trim().Replace('/', '.');
    }

    /// <summary>
    /// Normalize a subject honoring <see cref="NatsTransportConfiguration.NormalizeSubjects"/>: when the flag
    /// is enabled (the default) '/' separators are converted to NATS '.' tokens; when disabled the subject is
    /// only trimmed, so callers can use literal subjects containing '/'.
    /// </summary>
    internal string NormalizeSubjectIfEnabled(string subject)
    {
        return Configuration.NormalizeSubjects ? NormalizeSubject(subject) : subject.Trim();
    }

    /// <summary>
    /// Create a JetStream context on the shared connection honoring the configured
    /// <see cref="NatsTransportConfiguration.JetStreamDomain"/> / <see cref="NatsTransportConfiguration.JetStreamApiPrefix"/>.
    /// </summary>
    internal INatsJSContext CreateJetStreamContext() => CreateJetStreamContext(Connection);

    /// <summary>
    /// Create a JetStream context on the given connection honoring the configured JetStream domain / API prefix.
    /// All JetStream context creation flows through this factory so domain / leaf-node setups work uniformly
    /// (including per-tenant connections). When neither is configured the result is identical to the client
    /// default (<c>connection.CreateJetStreamContext()</c>).
    /// </summary>
    internal INatsJSContext CreateJetStreamContext(NatsConnection connection)
    {
        var domain = Configuration.JetStreamDomain;
        var apiPrefix = Configuration.JetStreamApiPrefix;

        if (string.IsNullOrWhiteSpace(domain) && string.IsNullOrWhiteSpace(apiPrefix))
        {
            return connection.CreateJetStreamContext();
        }

        // NatsJSOpts forbids setting both ApiPrefix and Domain; when both are supplied domain wins.
        var jsOpts = string.IsNullOrWhiteSpace(domain)
            ? new NatsJSOpts(connection.Opts, apiPrefix: apiPrefix)
            : new NatsJSOpts(connection.Opts, domain: domain);

        return connection.CreateJetStreamContext(jsOpts);
    }

    /// <summary>
    /// Resolve the NATS connection for a tenant: the tenant's own dedicated connection (created during
    /// <see cref="ConnectAsync"/>) when it declares its own connection string / credentials, otherwise the
    /// shared transport connection.
    /// </summary>
    internal NatsConnection GetTenantConnection(NatsTenant tenant)
    {
        return tenant.HasOwnConnection ? tenant.Connection ?? Connection : Connection;
    }

    private static NatsOpts buildTenantNatsOpts(NatsTenant tenant)
    {
        // The tenant carries its own full connection configuration (URL + any of the NATS auth mechanisms +
        // TLS), so we reuse the same ToNatsOpts() the shared connection uses rather than privileging one
        // credential kind. Only the client name is decorated so tenant connections are distinguishable.
        var opts = tenant.ConnectionConfiguration!.ToNatsOpts();
        return opts with { Name = $"{opts.Name}-tenant-{tenant.TenantId}" };
    }

    /// <summary>
    /// Extract the NATS subject from a Wolverine NATS endpoint URI of the form
    /// <c>{scheme}://subject/{subject}</c>. The scheme is intentionally not validated against a fixed
    /// literal: named brokers (see <c>AddNamedNatsBroker</c>) carry the broker name as the scheme, and
    /// routing to the correct transport instance has already happened by scheme before this is reached.
    /// </summary>
    public static string ExtractSubjectFromUri(Uri uri)
    {
        var path = uri.LocalPath.Trim('/');
        return string.IsNullOrEmpty(path) ? uri.Host : path;
    }

    public NatsEndpoint EndpointForSubject(string subject)
    {
        var normalized = NormalizeSubjectIfEnabled(subject);
        return _endpoints[normalized];
    }

    /// <summary>
    /// Base subject for on-demand topic-routed sending endpoints created by
    /// <c>PublishMessagesToNatsSubject&lt;T&gt;</c>. The real destination is the per-message
    /// subject stamped onto <see cref="Envelope.TopicName"/>; this is only a base/fallback.
    /// </summary>
    internal const string TopicSenderSubject = "wolverine.topics";

    private int _topicSenderIndex;

    /// <summary>
    /// Create a new topic-routed (<see cref="RoutingMode.ByTopic"/>) sending endpoint so
    /// messages can be published to a per-message subject computed at send time. Mirrors the
    /// MQTT transport's <c>NewTopicSender</c>; each call returns a distinct endpoint so multiple
    /// subject-source functions can coexist. Being <c>ByTopic</c> also enrolls the endpoint in
    /// <see cref="IMessageBus.BroadcastToTopicAsync"/>.
    /// </summary>
    internal NatsEndpoint NewTopicSender()
    {
        var subject = $"{TopicSenderSubject}.{++_topicSenderIndex}";
        var endpoint = _endpoints[subject];
        endpoint.RoutingType = RoutingMode.ByTopic;
        return endpoint;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var tenant in Tenants.Where(x => x.Connection != null))
        {
            try
            {
                await tenant.Connection!.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error disposing NATS connection for tenant {TenantId}", tenant.TenantId);
            }
        }

        try
        {
            if (_connection != null)
            {
                await _connection.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error disposing NATS connection");
        }
    }

    private async Task ProvisionStreamsAsync(INatsJSContext js)
    {
        _logger?.LogInformation(
            "Provisioning {Count} configured streams",
            Configuration.Streams.Count
        );

        foreach (var (name, config) in Configuration.Streams)
        {
            try
            {
                var exists = false;
                try
                {
                    await js.GetStreamAsync(name);
                    exists = true;
                    _logger?.LogDebug("Stream {StreamName} already exists", name);
                }
                catch (NatsJSException)
                {
                }

                if (!exists)
                {
                    var streamConfig = new StreamConfig(name, config.Subjects)
                    {
                        Retention = config.Retention,
                        Storage = config.Storage,
                        MaxMsgs = config.MaxMessages ?? -1,
                        MaxBytes = config.MaxBytes ?? -1,
                        MaxAge = config.MaxAge ?? TimeSpan.Zero,
                        MaxMsgsPerSubject = config.MaxMessagesPerSubject ?? 0,
                        Discard = config.DiscardPolicy,
                        NumReplicas = config.Replicas,
                        DuplicateWindow = config.DuplicateWindow ?? Configuration.JetStreamDefaults.DuplicateWindow,
                        AllowRollupHdrs = config.AllowRollup,
                        AllowDirect = config.AllowDirect,
                        DenyDelete = config.DenyDelete,
                        DenyPurge = config.DenyPurge,
                        AllowMsgSchedules = config.AllowMsgSchedules
                    };

                    await js.CreateStreamAsync(streamConfig);
                    _logger?.LogInformation(
                        "Created stream {StreamName} with subjects: {Subjects}",
                        name,
                        string.Join(", ", config.Subjects)
                    );
                }
                else
                {
                    _logger?.LogDebug(
                        "Stream {StreamName} already exists, skipping creation",
                        name
                    );
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to provision stream {StreamName}", name);
                throw new InvalidOperationException($"Failed to provision stream '{name}'", ex);
            }
        }
    }
}
