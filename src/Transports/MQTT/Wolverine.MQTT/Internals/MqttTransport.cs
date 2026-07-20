using ImTools;
using JasperFx.Core;
using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Formatter;
using System.Net;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.MQTT.Internals;

public class MqttTransport : TransportBase<MqttTopic>, IAsyncDisposable
{
    [IgnoreDescription]
    public LightweightCache<string, MqttTopic> Topics { get; } = new();

    // Named broker + broker-per-tenant (GH-3307): listener bookkeeping is per-managed-client rather than
    // transport-global, because each tenant runs on its own IManagedMqttClient and messages received on a
    // tenant connection must resolve to that tenant's (tenant-id stamping) listener, not the default one.
    private readonly Dictionary<IManagedMqttClient, ClientListenerGroup> _clientGroups = new();
    private ILogger<MqttTransport> _logger = null!;
    private CancellationTokenSource? _refreshCts;

    // Broker-per-tenant (GH-3307): CancellationTokenSources for each tenant client's JWT re-auth loop, so they
    // can be cancelled on shutdown.
    private readonly List<CancellationTokenSource> _tenantRefreshCts = new();

    public static string TopicForUri(Uri uri)
    {
        if (uri == null) return string.Empty;
        return uri.LocalPath.Trim('/');
    }

    public MqttTransport() : this("mqtt")
    {
    }

    /// <summary>
    /// Constructor used when connecting to more than one MQTT broker from a single application. The
    /// <paramref name="protocol"/> doubles as the additional broker's URI scheme so its endpoints don't
    /// collide with the default <c>mqtt://</c> broker. Reached through
    /// <see cref="TransportCollection.GetOrCreate{T}"/> when a <see cref="BrokerName"/> is supplied.
    /// </summary>
    public MqttTransport(string protocol) : base(protocol, "MQTT Transport", [protocol])
    {
        Topics.OnMissing = topicName => new MqttTopic(topicName, this, EndpointRole.Application);
    }

    /// <summary>
    /// Broker-per-tenant registrations (GH-3307). Each tenant owns its own dedicated <see cref="IManagedMqttClient"/>
    /// pointed at its own broker while sharing the topic topology declared on the parent transport; outbound is
    /// routed by <see cref="Envelope.TenantId"/> and inbound listeners stamp the tenant id.
    /// </summary>
    [IgnoreDescription]
    internal LightweightCache<string, MqttTenant> Tenants { get; } = new(name => new MqttTenant(name));

    /// <summary>
    /// Controls how an outbound message whose tenant id is null or unregistered is routed when broker-per-tenant
    /// multi-tenancy is in effect. Defaults to <see cref="TenantedIdBehavior.FallbackToDefault"/>.
    /// </summary>
    public TenantedIdBehavior TenantedIdBehavior { get; set; } = TenantedIdBehavior.FallbackToDefault;

    /// <summary>
    /// The managed client that a tenant's traffic should flow over, falling back to the default/shared client for
    /// tenants that were never registered (or the untenanted path).
    /// </summary>
    internal IManagedMqttClient GetTenantClient(MqttTenant tenant) => tenant.Client ?? Client;

    private sealed class ClientListenerGroup
    {
        public List<MqttListener> Listeners { get; } = new();
        public ImHashMap<string, MqttListener> TopicListeners = ImHashMap<string, MqttListener>.Empty;
        public bool Subscribed;
    }

    private ClientListenerGroup groupFor(IManagedMqttClient client)
    {
        if (!_clientGroups.TryGetValue(client, out var group))
        {
            group = new ClientListenerGroup();
            _clientGroups[client] = group;
        }

        return group;
    }

    protected override IEnumerable<MqttTopic> endpoints()
    {
        return Topics;
    }

    protected override MqttTopic findEndpointByUri(Uri uri)
    {
        var topicName = TopicForUri(uri);
        return Topics[topicName];
    }

    public string ResponseTopic { get; private set; } = "wolverine/response";

    public override async ValueTask InitializeAsync(IWolverineRuntime runtime)
    {
        // The per-node reply topic must be unique to this running node. In Solo mode the assigned
        // node number is always 1 (#3188), so several Solo services on one broker would collide on
        // the same topic and cross-deliver each other's replies — use the always unique
        // UniqueNodeId instead. Balanced nodes get a unique AssignedNodeNumber via election, so they
        // keep the existing, more readable topic. See #3189.
        var responseNode = runtime.Options.Durability.Mode == DurabilityMode.Solo
            ? runtime.Options.UniqueNodeId.ToString("N")
            : runtime.Options.Durability.AssignedNodeNumber.ToString();
        ResponseTopic = "wolverine/response/" + responseNode;
        var mqttTopic = Topics[ResponseTopic];
        mqttTopic.IsUsedForReplies = true;
        mqttTopic.IsListener = true;

        var topics = Topics[MqttTopic.WolverineTopicsName];
        topics.RoutingType = RoutingMode.ByTopic;
        topics.OutgoingRules.Add(new TopicRoutingRule()); // this will make any messages use the auto resolved topic name

        _logger = runtime.LoggerFactory.CreateLogger<MqttTransport>();

        var mqttFactory = new MqttClientFactory();

        var logger = new MqttNetLogger(runtime.LoggerFactory.CreateLogger<MqttClient>());
        Client = mqttFactory.CreateManagedMqttClient(logger);

        Options.ClientOptions.ProtocolVersion = MqttProtocolVersion.V500;
        if (JwtAuthenticationOptions is not null)
        {
            Options.ClientOptions.AuthenticationMethod = "OAUTH2-JWT";
            Options.ClientOptions.AuthenticationData = await JwtAuthenticationOptions.GetTokenCallBack();
        }

        Client.ConnectedAsync += onClientConnected;
        Client.DisconnectedAsync += onClientDisconnected;
        await Client.StartAsync(Options);

        // Broker-per-tenant (GH-3307): each tenant gets its own dedicated managed client, connected to the
        // tenant's own broker. MQTT brokers forcibly disconnect a second connection that shares a ClientId, so
        // each tenant client is given a UNIQUE ClientId derived from the tenant id (see buildTenantClientAsync).
        foreach (var tenant in Tenants)
        {
            tenant.Client = await buildTenantClientAsync(runtime, mqttFactory, tenant);
        }

        foreach (var endpoint in Topics)
        {
            endpoint.Compile(runtime);
        }
    }

    private async Task<IManagedMqttClient> buildTenantClientAsync(IWolverineRuntime runtime, MqttClientFactory mqttFactory,
        MqttTenant tenant)
    {
        var logger = new MqttNetLogger(runtime.LoggerFactory.CreateLogger<MqttClient>());
        var client = mqttFactory.CreateManagedMqttClient(logger);

        var options = tenant.Options;
        options.ClientOptions.ProtocolVersion = MqttProtocolVersion.V500;

        // Enforce a unique ClientId for the tenant connection even if the user pre-set one on the tenant options.
        // Two managed clients sharing a ClientId make the broker kick one of them, so tenant traffic would drop.
        var baseClientId = options.ClientOptions.ClientId;
        options.ClientOptions.ClientId =
            (string.IsNullOrEmpty(baseClientId) ? Guid.NewGuid().ToString("N") : baseClientId)
            + "-tenant-" + tenant.TenantId;

        if (tenant.Jwt is not null)
        {
            options.ClientOptions.AuthenticationMethod = "OAUTH2-JWT";
            options.ClientOptions.AuthenticationData = await tenant.Jwt.GetTokenCallBack();
            client.ConnectedAsync += buildTenantJwtRefreshHandler(client, tenant.Jwt);
        }

        await client.StartAsync(options);

        return client;
    }

    // Broker-per-tenant JWT re-authentication loop, scoped to a single tenant client. Mirrors the default
    // client's onClientConnected refresh, but keyed to the tenant's own connection and token callback.
    private Func<MqttClientConnectedEventArgs, Task> buildTenantJwtRefreshHandler(IManagedMqttClient client,
        MqttJwtAuthenticationOptions jwt)
    {
        CancellationTokenSource? refreshCts = null;
        return async arg =>
        {
            if (arg.ConnectResult.ResultCode != MqttClientConnectResultCode.Success)
            {
                return;
            }

            if (refreshCts is not null)
            {
                await refreshCts.CancelAsync();
                refreshCts.Dispose();
            }

            refreshCts = new CancellationTokenSource();
            lock (_tenantRefreshCts)
            {
                _tenantRefreshCts.Add(refreshCts);
            }

            var ct = refreshCts.Token;
            _ = Task.Run(async () =>
            {
                using var periodicTimer = new PeriodicTimer(jwt.RefreshPeriod);
                try
                {
                    while (await periodicTimer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                    {
                        if (!client.IsConnected) continue;

                        await client.InternalClient
                            .SendEnhancedAuthenticationExchangeDataAsync(
                                new MqttEnhancedAuthenticationExchangeData
                                {
                                    AuthenticationData = await jwt.GetTokenCallBack(),
                                    ReasonCode = MQTTnet.Protocol.MqttAuthenticateReasonCode.ReAuthenticate
                                }, ct)
                            .ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Shutdown requested
                }
            }, ct);
        };
    }

    public WolverineTransportHealthCheck BuildHealthCheck(IWolverineRuntime runtime)
    {
        return new MqttHealthCheck(this);
    }

    private void startSubscribing(IManagedMqttClient client)
    {
        var group = groupFor(client);
        if (group.Subscribed) return;

        client.ApplicationMessageReceivedAsync += arg => receiveAsync(client, arg);
        group.Subscribed = true;
    }

    private Task receiveAsync(IManagedMqttClient client, MqttApplicationMessageReceivedEventArgs arg)
    {
        var topicName = arg.ApplicationMessage.Topic;
        if (tryFindListener(client, topicName, out var listener))
        {
            return listener.ReceiveAsync(arg);
        }
        else
        {
            _logger?.LogInformation("Received MQTT message for topic {TopicName} that has no listener attached", topicName);
            return Task.CompletedTask;
        }
    }

    private async Task onClientConnected(MqttClientConnectedEventArgs arg)
    {
        if (arg.ConnectResult.ResultCode != MqttClientConnectResultCode.Success
            || JwtAuthenticationOptions == null)
        {
            return;
        }

        if (_refreshCts is not null)
        {
            await _refreshCts.CancelAsync();
            _refreshCts.Dispose();
        }

        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;
        _ = Task.Run(async () =>
        {
            using var periodicTimer = new PeriodicTimer(JwtAuthenticationOptions.RefreshPeriod);
            try
            {
                while (await periodicTimer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    if (!Client.IsConnected) continue;

                    await Client.InternalClient
                        .SendEnhancedAuthenticationExchangeDataAsync(
                            new MqttEnhancedAuthenticationExchangeData
                            {
                                AuthenticationData = await JwtAuthenticationOptions.GetTokenCallBack(),
                                ReasonCode = MQTTnet.Protocol.MqttAuthenticateReasonCode.ReAuthenticate
                            }, ct)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Shutdown requested
            }
        }, ct);
    }

    private async Task onClientDisconnected(MqttClientDisconnectedEventArgs arg)
    {
        if (_refreshCts is not null)
        {
            await _refreshCts.CancelAsync();
            _refreshCts.Dispose();
            _refreshCts = null;
        }
    }

    internal bool tryFindListener(IManagedMqttClient client, string topicName, out MqttListener listener)
    {
        var group = groupFor(client);
        if (group.TopicListeners.TryFind(topicName, out listener))
        {
            return listener is not null;
        }

        listener = (group.Listeners.FirstOrDefault(x => x.TopicName == topicName) ?? group.Listeners.FirstOrDefault(x =>
            MqttTopicFilterComparer.Compare(topicName, x.TopicName) == MqttTopicFilterCompareResult.IsMatch))!;

        group.TopicListeners = group.TopicListeners.AddOrUpdate(topicName, listener!);


        return listener is not null;
    }

    internal IManagedMqttClient Client { get; set; } = null!;
    internal MqttJwtAuthenticationOptions? JwtAuthenticationOptions { get; set; }

    // GH-3269: ManagedMqttClientOptions.ClientOptions carries Credentials (username/password) and other secrets.
    // Reflecting it into the diagnostic tree would leak them, so it is suppressed; the sanitized host:port target is
    // surfaced via DescribeEndpoint() instead.
    [IgnoreDescription]
    public ManagedMqttClientOptions Options { get; set; } = new ManagedMqttClientOptions
        { ClientOptions = new MqttClientOptions() };

    public override string? DescribeEndpoint()
    {
        switch (Options.ClientOptions?.ChannelOptions)
        {
            case MqttClientTcpOptions { RemoteEndpoint: DnsEndPoint dns }:
                return $"{dns.Host}:{dns.Port}";
            case MqttClientTcpOptions { RemoteEndpoint: IPEndPoint ip }:
                return $"{ip.Address}:{ip.Port}";
            case MqttClientWebSocketOptions ws:
                // The websocket URI is the endpoint; credentials ride on Credentials, not the URI.
                return ws.Uri;
            default:
                return null;
        }
    }

    public override Endpoint ReplyEndpoint()
    {
        return Topics[ResponseTopic];
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_refreshCts is not null)
            {
                await _refreshCts.CancelAsync();
                _refreshCts.Dispose();
            }

            CancellationTokenSource[] tenantCts;
            lock (_tenantRefreshCts)
            {
                tenantCts = _tenantRefreshCts.ToArray();
                _tenantRefreshCts.Clear();
            }

            foreach (var cts in tenantCts)
            {
                try
                {
                    await cts.CancelAsync();
                    cts.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            if (Client is not null)
                await Client.StopAsync();

            // Broker-per-tenant (GH-3307): each tenant owns its own managed client; stop them too.
            foreach (var tenant in Tenants)
            {
                if (tenant.Client is not null)
                {
                    await tenant.Client.StopAsync();
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal async ValueTask SubscribeToTopicAsync(string topicName, MqttListener listener, MqttTopic mqttTopic,
        IManagedMqttClient client)
    {
        var group = groupFor(client);
        group.Listeners.Add(listener);

        await client.SubscribeAsync(topicName, mqttTopic.QualityOfServiceLevel);

        startSubscribing(client);
    }

    private int _senderIndex = 0;

    internal MqttTopic NewTopicSender()
    {
        var topicName = MqttTopic.WolverineTopicsName + (++_senderIndex);
        var topic = Topics[topicName];
        topic.RoutingType = RoutingMode.ByTopic;
        return topic;
    }
}
