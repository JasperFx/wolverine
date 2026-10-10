using System.Collections.Concurrent;
using System.Text;
using JasperFx.Core;
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

    /// <summary>
    /// The reply subject a request carries on the wire. A request answered through this node's own
    /// <see cref="ResponseSubject"/> gets its envelope id appended as one more token, which the reply listener's
    /// <c>{ResponseSubject}.&gt;</c> subscription covers. NATS answers a request that reaches no subscriber with a
    /// "no responders" status message on exactly that subject, and the token is what ties it back to the waiting
    /// <c>InvokeAsync()</c>. Wolverine responders reply to the <c>reply-uri</c> header instead, which stays the
    /// plain <see cref="ResponseSubject"/>.
    /// </summary>
    internal string WireReplySubjectFor(Envelope envelope)
    {
        var subject = ExtractSubjectFromUri(envelope.ReplyUri!);
        return subject == ResponseSubject ? $"{subject}.{envelope.Id:N}" : subject;
    }

    private static readonly TimeSpan DroppedMessageWarningInterval = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, DroppedMessageCounter> _droppedMessages = new();

    private sealed class DroppedMessageCounter
    {
        // Far enough in the past that the first drop is always reported
        public long LastWarnedAt = Environment.TickCount64 - 2 * (long)DroppedMessageWarningInterval.TotalMilliseconds;
        public long SinceLastWarning;
        public long Total;
    }

    private static readonly TimeSpan WireReplySubjectLifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<Guid, WireReplySubject> _wireReplySubjects = new();
    private int _wireReplySubjectWrites;

    private readonly record struct WireReplySubject(string BaseSubject, string Subject, long RememberedAt);

    /// <summary>
    /// The other half of <see cref="WireReplySubjectFor"/>, on the responding side. A responder whose NATS user may
    /// only publish responses (<c>allow_responses</c>) may answer on exactly the reply subject the request carried,
    /// while the request's <c>reply-uri</c> header names the plain response subject. So remember the wire subject
    /// of a request received with a per-request token, for <see cref="ReplySubjectFor"/>. Replies take their entry;
    /// entries of requests that are never answered expire.
    /// </summary>
    internal void RememberWireReplySubject(Envelope request, string? wireReplySubject)
    {
        if (request.ReplyUri == null || string.IsNullOrEmpty(wireReplySubject))
        {
            return;
        }

        var baseSubject = ExtractSubjectFromUri(request.ReplyUri);
        if (wireReplySubject != $"{baseSubject}.{request.Id:N}")
        {
            return;
        }

        _wireReplySubjects[request.Id] = new WireReplySubject(baseSubject, wireReplySubject, Environment.TickCount64);

        if (Interlocked.Increment(ref _wireReplySubjectWrites) % 1024 == 0)
        {
            var expired = Environment.TickCount64 - (long)WireReplySubjectLifetime.TotalMilliseconds;
            foreach (var pair in _wireReplySubjects)
            {
                if (pair.Value.RememberedAt < expired)
                {
                    _wireReplySubjects.TryRemove(pair);
                }
            }
        }
    }

    /// <summary>
    /// Where to publish a message bound for <paramref name="targetSubject"/>: the remembered wire reply subject when
    /// the message answers a request (a response or an acknowledgement shares the request's id as its
    /// <see cref="Envelope.ConversationId"/>) whose replies go to <paramref name="targetSubject"/>, otherwise
    /// <paramref name="targetSubject"/> itself. Other messages of the same conversation are left alone.
    /// </summary>
    internal string ReplySubjectFor(Envelope envelope, string targetSubject)
    {
        if (envelope.ConversationId != Guid.Empty &&
            _wireReplySubjects.TryGetValue(envelope.ConversationId, out var remembered) &&
            remembered.BaseSubject == targetSubject)
        {
            return remembered.Subject;
        }

        return targetSubject;
    }

    /// <summary>
    /// The reply to the request <paramref name="envelope"/> answers has reached the wire, so its entry is no
    /// longer needed. Kept separate from <see cref="ReplySubjectFor"/> on purpose: a reply whose publish failed
    /// is retried by the sending agent, and that retry has to find the per-request subject again -- the plain
    /// response subject is exactly what a responder limited to <c>allow_responses</c> may not publish to.
    /// </summary>
    internal void ForgetWireReplySubject(Envelope envelope)
    {
        if (envelope.ConversationId != Guid.Empty)
        {
            _wireReplySubjects.TryRemove(envelope.ConversationId, out _);
        }
    }

    /// <summary>
    /// GH-4279. Make an arbitrary string safe to use as ONE token of a NATS subject. Deliberately not a
    /// <c>SanitizeIdentifier</c> override: that runs over every identifier the user names, and '.' is a
    /// legal, meaningful token separator in a subject they wrote on purpose. Only a value Wolverine
    /// splices into a subject it composes -- the service name -- needs flattening, because a '.' there
    /// would silently add a token and '*' or '>' would turn the reply subject into a wildcard.
    /// </summary>
    internal static string sanitizeSubjectToken(string? token)
    {
        if (token.IsEmpty()) return "wolverine";

        var builder = new StringBuilder(token!.Length);
        foreach (var c in token!)
        {
            builder.Append(c is '.' or '*' or '>' || char.IsWhiteSpace(c) || char.IsControl(c) ? '_' : c);
        }

        return builder.ToString();
    }

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
        _connection ?? throw new InvalidOperationException(
            "The NATS connection has not been created. Either UseNats() was never called on WolverineOptions, or the Wolverine host has not been started yet -- the connection is opened during host startup.");

    [IgnoreDescription]
    public INatsJSContext JetStreamContext =>
        _jetStreamContext
        ?? throw new InvalidOperationException(
            $"The NATS JetStream context has not been created. Either UseNats() was never called on WolverineOptions, the Wolverine host has not been started yet, or JetStream is disabled by {nameof(NatsTransportConfiguration)}.{nameof(NatsTransportConfiguration.EnableJetStream)} = false.");

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
        // GH-4279: the service name too. AssignedNodeNumber is unique within ONE application's node
        // cluster -- the election runs against that application's own message store -- so two unrelated
        // applications sharing a NATS cluster each elect a node 1 and both subscribed to
        // "wolverine.response.1". Core NATS fans out, so each received the other's reply payloads; and if
        // both had set the same DefaultQueueGroup, the two subscriptions land in one queue group on one
        // subject and NATS load-balances instead, so roughly half of one application's replies were
        // delivered to the other and never reached the caller. Azure Service Bus, SQS and Redis all put
        // the service name in this name already; NATS was the only transport with neither that nor a
        // process-unique token.
        ResponseSubject = $"wolverine.response.{sanitizeSubjectToken(runtime.Options.ServiceName)}.{responseNode}";
        var responseEndpoint = _endpoints[ResponseSubject];
        responseEndpoint.IsUsedForReplies = true;
        responseEndpoint.IsListener = true;

        // GH-4860. ConnectAsync is re-run on every retry of a failed start and at the start of every
        // BrokerResource operation -- AutoProvision's resource setup runs it right after the start-up run,
        // while the listeners and senders built in between hold the first connection. Each run used to open a
        // fresh NatsConnection, and a fresh one per tenant with its own connection, on top of the previous
        // without disposing it, so a broker that took a few attempts to reach left that many half-open client
        // connections behind. A connection that is alive is reused; only one a previous run left dead -- a
        // failed connect, a close -- is disposed and replaced.
        if (_connection is { ConnectionState: not NatsConnectionState.Closed })
        {
            _logger.LogDebug("Reusing the open NATS connection to {Url}", Configuration.ConnectionString);
        }
        else
        {
            await disposeConnectionsAsync();

            var natsOpts = Configuration.ToNatsOpts();
            natsOpts = natsOpts with { Name = $"wolverine-{runtime.Options.ServiceName}" };
            natsOpts = Configuration.ConfigureNatsOpts?.Invoke(natsOpts) ?? natsOpts;
            _connection = new NatsConnection(natsOpts);
            logDroppedMessages(_connection);
            await _connection.ConnectAsync();

            _logger.LogInformation("Connected to NATS at {Url}", Configuration.ConnectionString);
        }
        
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

        // Verify creates nothing, so it has to run even with AutoProvision off -- the natural companion setting
        // for streams managed outside the application, and the case Verify exists for
        var autoProvisionStreams = Configuration.Streams.Any() &&
                                   (Configuration.AutoProvision ||
                                    Configuration.StreamProvisioning == NatsProvisioning.Verify);

        // Collected over the shared and every tenant connection, so Verify reports all of it at once
        var deviations = new List<string>();

        if (Configuration.EnableJetStream)
        {
            _jetStreamContext = CreateJetStreamContext();
            _logger.LogInformation("JetStream context initialized");

            if (autoProvisionStreams)
            {
                deviations.AddRange(await ProvisionStreamsAsync(_jetStreamContext));
            }
        }

        // Tenants that declare their own connection string / credentials get a dedicated connection they
        // own for the lifetime of the transport; the NATS client connects lazily on first use. Tenants
        // without their own connection reuse the shared connection above (subject-prefix isolation only).
        foreach (var tenant in Tenants.Where(x => x.HasOwnConnection))
        {
            // Same rule as the shared connection: a tenant connection a previous run opened is kept
            if (tenant.Connection is { ConnectionState: not NatsConnectionState.Closed })
            {
                continue;
            }

            var tenantConnection = new NatsConnection(buildTenantNatsOpts(tenant));
            logDroppedMessages(tenantConnection);
            tenant.Connection = tenantConnection;
            _logger.LogInformation("Created dedicated NATS connection for tenant {TenantId}", tenant.TenantId);

            // Each tenant server is its own JetStream instance, so mirror the configured streams onto it
            // (the streams the shared connection just provisioned don't exist on the tenant's server).
            if (Configuration.EnableJetStream && autoProvisionStreams)
            {
                var tenantDeviations = await ProvisionStreamsAsync(CreateJetStreamContext(tenantConnection));
                deviations.AddRange(tenantDeviations.Select(x => $"{x} (tenant '{tenant.TenantId}')"));
            }
        }

        if (deviations.Count > 0)
        {
            throw new InvalidOperationException(
                $"NATS provisioning is set to {nameof(NatsProvisioning.Verify)}, and the JetStream server does not match the declared streams:{Environment.NewLine}- " +
                string.Join($"{Environment.NewLine}- ", deviations));
        }
    }

    /// <summary>
    /// A core NATS subscription buffers incoming messages in a bounded channel
    /// (<see cref="NatsOpts.SubPendingChannelCapacity"/>), and under the default
    /// <see cref="NatsOpts.SubPendingChannelFullMode"/> of <c>DropNewest</c> a full channel drops messages. Core NATS
    /// never redelivers them, and NATS.Net reports the loss only through these connection events and its own
    /// logger, which Wolverine does not wire up -- so without this the loss is invisible.
    /// </summary>
    private void logDroppedMessages(NatsConnection connection)
    {
        connection.MessageDropped += (_, args) =>
        {
            // One warning per subscription per interval, with the count: a slow consumer under a burst drops
            // thousands of messages a second, and a warning for each of them would be its own load on the box
            var counter = _droppedMessages.GetOrAdd($"{connection.Opts.Name}|{args.Subscription.Subject}",
                _ => new DroppedMessageCounter());
            var total = Interlocked.Increment(ref counter.Total);
            Interlocked.Increment(ref counter.SinceLastWarning);

            var now = Environment.TickCount64;
            var lastWarnedAt = Volatile.Read(ref counter.LastWarnedAt);
            if (now - lastWarnedAt < (long)DroppedMessageWarningInterval.TotalMilliseconds ||
                Interlocked.CompareExchange(ref counter.LastWarnedAt, now, lastWarnedAt) != lastWarnedAt)
            {
                return ValueTask.CompletedTask;
            }

            var sinceLastWarning = Interlocked.Exchange(ref counter.SinceLastWarning, 0);
            _logger?.LogWarning(
                "NATS connection {Connection} dropped a message on subject {Subject}: the pending channel of subscription {Subscription} is full ({Pending} pending), and core NATS does not redeliver it. {Dropped} message(s) dropped on this subscription since the last warning, {Total} in all; further drops are reported at most every {Interval}. Raise the pending channel capacity (NatsOpts.SubPendingChannelCapacity) or scale out the listener",
                connection.Opts.Name,
                args.Subject,
                args.Subscription.Subject,
                args.Pending,
                sinceLastWarning,
                total,
                DroppedMessageWarningInterval);
            return ValueTask.CompletedTask;
        };

        connection.SlowConsumerDetected += (_, args) =>
        {
            _logger?.LogWarning(
                "NATS subscription {Subscription} on connection {Connection} is a slow consumer: messages arrive faster than they are processed, and messages that do not fit into its pending channel of {Capacity} are dropped",
                args.Subscription.Subject,
                connection.Opts.Name,
                connection.Opts.SubPendingChannelCapacity);
            return ValueTask.CompletedTask;
        };
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

    private NatsOpts buildTenantNatsOpts(NatsTenant tenant)
    {
        // The tenant carries its own full connection configuration (URL + any of the NATS auth mechanisms +
        // TLS), so we reuse the same ToNatsOpts() the shared connection uses rather than privileging one
        // credential kind. Only the client name is decorated so tenant connections are distinguishable.
        var configuration = tenant.ConnectionConfiguration!;
        var opts = configuration.ToNatsOpts();
        opts = opts with { Name = $"{opts.Name}-tenant-{tenant.TenantId}" };

        // The tenant's configuration is a copy of the transport's taken by AddTenant(), so a hook registered
        // after that call only lives on the transport's configuration -- fall back to it
        var configure = configuration.ConfigureNatsOpts ?? Configuration.ConfigureNatsOpts;
        return configure?.Invoke(opts) ?? opts;
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

    public ValueTask DisposeAsync()
    {
        return disposeConnectionsAsync();
    }

    /// <summary>
    /// Dispose the shared connection and every tenant's dedicated connection, and forget them. Shared by
    /// DisposeAsync and by ConnectAsync, which has to release whatever a previous attempt opened before it
    /// opens again (GH-4860)
    /// </summary>
    private async ValueTask disposeConnectionsAsync()
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

            tenant.Connection = null;
        }

        var connection = _connection;
        _connection = null;
        if (connection == null)
        {
            return;
        }

        try
        {
            await connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error disposing NATS connection");
        }
    }

    /// <summary>
    /// Create the declared streams that are missing, and handle the ones that exist as
    /// <see cref="NatsTransportConfiguration.Provisioning"/> says.
    /// </summary>
    /// <returns>Every deviation <see cref="NatsProvisioning.Verify"/> found; empty in the other modes</returns>
    private async Task<IReadOnlyList<string>> ProvisionStreamsAsync(INatsJSContext js)
    {
        var provisioning = Configuration.StreamProvisioning;
        var deviations = new List<string>();

        _logger?.LogInformation(
            "Provisioning {Count} configured streams",
            Configuration.Streams.Count
        );

        foreach (var (name, config) in Configuration.Streams)
        {
            try
            {
                var desired = JetStreamProvisioning.BuildStreamConfig(name, config, Configuration.JetStreamDefaults);

                StreamConfig? existing = null;
                try
                {
                    existing = (await js.GetStreamAsync(name)).Info.Config;
                    _logger?.LogDebug("Stream {StreamName} already exists", name);
                }
                catch (NatsJSApiException e) when (e.Error.Code == 404)
                {
                    // Only "stream not found" means the stream is missing. Any other failure -- no JetStream
                    // answering for the domain, missing permissions, a timeout -- propagates instead of being
                    // reported as a missing stream under Verify or answered with a create
                }

                if (existing == null)
                {
                    if (provisioning == NatsProvisioning.Verify)
                    {
                        deviations.Add($"stream '{name}' does not exist");
                        continue;
                    }

                    await js.CreateStreamAsync(desired);
                    _logger?.LogInformation(
                        "Created stream {StreamName} with subjects: {Subjects}",
                        name,
                        string.Join(", ", config.Subjects)
                    );
                    continue;
                }

                if (provisioning == NatsProvisioning.CreateOnly)
                {
                    _logger?.LogDebug(
                        "Stream {StreamName} already exists, skipping creation",
                        name
                    );
                    continue;
                }

                var differences = JetStreamProvisioning.CompareStream(desired, existing);
                if (differences.Count == 0)
                {
                    _logger?.LogDebug("Stream {StreamName} already matches its configuration", name);
                }
                else if (provisioning == NatsProvisioning.CreateOrUpdate)
                {
                    await js.UpdateStreamAsync(JetStreamProvisioning.OverlayManagedSettings(existing, desired));
                    _logger?.LogInformation(
                        "Updated stream {StreamName}: {Differences}",
                        name,
                        string.Join("; ", differences)
                    );
                }
                else
                {
                    deviations.AddRange(differences.Select(x => $"stream '{name}': {x}"));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to provision stream {StreamName}", name);
                throw new InvalidOperationException($"Failed to provision stream '{name}'", ex);
            }
        }

        return deviations;
    }
}
