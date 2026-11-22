using ImTools;
using JasperFx.Core;
using JasperFx.Descriptors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

    // GH-4802: a persistent session (CleanSession = false) means the broker still holds this client's
    // subscriptions, so it starts flushing their backlog the moment the connection comes up -- which is
    // before Wolverine has registered any listener. Volatile because it is written from the host's
    // ApplicationStarted callback and read on MQTTnet's receive thread.
    private volatile bool _listenersAreUp;

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

    private TenantedIdBehavior? _tenantedIdBehavior;
    private bool _v6DefaultsRestored;

    /// <summary>
    /// Controls how an outbound message whose tenant id is null or unregistered is routed when broker-per-tenant
    /// multi-tenancy is in effect. A message with no tenant id always goes to the default broker. Defaults to
    /// <see cref="Wolverine.Transports.Sending.TenantedIdBehavior.DeadLetterUnknownTenants"/> since Wolverine 7.0
    /// (GH-5196): a message for an unregistered tenant id is dead lettered, never sent to the default broker.
    /// <see cref="WolverineOptions.RestoreV6Defaults"/> restores the 6.x default,
    /// <see cref="Wolverine.Transports.Sending.TenantedIdBehavior.FallbackToDefault"/>. A value set here wins over both.
    /// </summary>
    public TenantedIdBehavior TenantedIdBehavior
    {
        get => _tenantedIdBehavior ?? (_v6DefaultsRestored
            ? TenantedIdBehavior.FallbackToDefault
            : TenantedIdBehavior.DeadLetterUnknownTenants);
        set => _tenantedIdBehavior = value;
    }

    /// <summary>
    /// The managed client that a tenant's traffic should flow over, falling back to the default/shared client for
    /// tenants that were never registered (or the untenanted path).
    /// </summary>
    internal IManagedMqttClient GetTenantClient(MqttTenant tenant) => tenant.Client ?? Client;

    private sealed class ClientListenerGroup
    {
        // Copy-on-write: read on MQTTnet's receive thread while a restarting listener replaces an entry
        public MqttListener[] Listeners = [];
        public ImHashMap<string, MqttListener> TopicListeners = ImHashMap<string, MqttListener>.Empty;
        public bool Subscribed;

        // GH-5790. Messages that arrived on this connection before their listener registered, still
        // unacknowledged. Guarded by HeldLock, which also covers listener registration so that a message is
        // either found by its listener's lookup or held for it, never neither.
        public readonly object HeldLock = new();
        public List<MqttApplicationMessageReceivedEventArgs> Held = new();
    }

    /// <summary>
    ///     GH-5790. The most messages one connection holds for listeners that have not registered yet. Past this,
    ///     a message that arrives before its listener is left unacknowledged and not held, so it stays in flight
    ///     on this connection until the session resumes on a new one (the GH-4802 behaviour). The broker's own
    ///     in-flight window normally keeps the count far lower.
    /// </summary>
    internal int MaximumHeldBeforeListeners { get; set; } = 1000;

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
        // GH-5196. Read when the transport starts, never at registration
        _v6DefaultsRestored = runtime.Options.V6DefaultsRestored;

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
            await ApplyJwtAuthenticationAsync(Options.ClientOptions, JwtAuthenticationOptions);
        }

        Client.ConnectedAsync += onClientConnected;
        Client.DisconnectedAsync += onClientDisconnected;

        // GH-4802: attach the receive handler BEFORE the connection is started. It used to be attached
        // by the first SubscribeToTopicAsync call, which does not happen until
        // Endpoints.StartListenersAsync() -- so a persistent session's backlog could arrive while
        // ApplicationMessageReceivedAsync had no handler at all. MQTTnet's AutoAcknowledge defaults to
        // true and only MqttListener.ReceiveAsync ever turns it off, so those messages were acknowledged
        // to the broker and dropped with no exception, no dead letter, and not even a log line.
        startSubscribing(Client);
        trackListenerStartup(runtime);

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
            await ApplyJwtAuthenticationAsync(options.ClientOptions, tenant.Jwt);
            client.ConnectedAsync += buildTenantJwtRefreshHandler(client, tenant.Jwt);
        }

        // GH-4802: as for the default connection, the receive handler has to be attached before the
        // tenant's own connection is started.
        startSubscribing(client);

        await client.StartAsync(options);

        return client;
    }

    // GH-3588: the authentication method name is broker specific. It defaults to "OAUTH2-JWT", but brokers like
    // Azure Event Grid expect their own name ("CUSTOM-JWT") for the very same token exchange, so it is configurable
    // on MqttJwtAuthenticationOptions. Applied identically to the default connection and to each tenant connection.
    internal static async Task ApplyJwtAuthenticationAsync(MqttClientOptions clientOptions,
        MqttJwtAuthenticationOptions jwt)
    {
        clientOptions.AuthenticationMethod = jwt.AuthenticationMethod;
        clientOptions.AuthenticationData = await jwt.GetTokenCallBack();
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

    /// <summary>
    /// GH-4802. ApplicationStarted fires only once every hosted service has started, and WolverineRuntime
    /// is one of them -- so by the time it runs, Endpoints.StartListenersAsync() has completed and every
    /// SubscribeToTopicAsync call this process is going to make has been made. That is the moment "no
    /// listener for this topic" stops meaning "not yet" and starts meaning "not ever".
    /// </summary>
    private void trackListenerStartup(IWolverineRuntime runtime)
    {
        var lifetime = runtime.Services.GetService<IHostApplicationLifetime>();
        if (lifetime == null)
        {
            // Nothing to hang the latch off, so never defer. Holding a message unacknowledged forever
            // would be a worse failure than the drop it replaces -- see ReceiveFromClientAsync.
            _listenersAreUp = true;
            return;
        }

        lifetime.ApplicationStarted.Register(MarkListenersAreUp);
    }

    /// <summary>
    ///     Every listener this process will register has registered. Hands anything still held to a listener
    ///     that can take it now, and discards the rest the way a message for a topic nothing listens to is
    ///     discarded after startup.
    /// </summary>
    internal void MarkListenersAreUp()
    {
        _listenersAreUp = true;

        // Every group was created while connecting and subscribing, all of which is finished by now
        var clients = _clientGroups.Keys.ToArray();

        foreach (var client in clients)
        {
            var group = groupFor(client);
            List<MqttApplicationMessageReceivedEventArgs> held;
            lock (group.HeldLock)
            {
                held = group.Held;
                group.Held = new List<MqttApplicationMessageReceivedEventArgs>();
            }

            foreach (var arg in held)
            {
                if (tryFindListener(client, arg.ApplicationMessage.Topic, out var listener))
                {
                    listener.ReceiveHeldMessages([arg]);
                }
                else
                {
                    _ = discardHeldAsync(arg);
                }
            }
        }
    }

    private async Task discardHeldAsync(MqttApplicationMessageReceivedEventArgs arg)
    {
        _logger?.LogWarning(
            "Wolverine has finished starting and no listener is attached for MQTT topic {TopicName}, so a message held for it since the connection came up is being discarded. The broker is most likely still holding a subscription from an earlier session for a topic this process does not listen to",
            arg.ApplicationMessage.Topic);

        try
        {
            // Held unacknowledged, so the broker still counts it in flight until this
            await arg.AcknowledgeAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Failed to acknowledge a discarded MQTT message for topic {TopicName}",
                arg.ApplicationMessage.Topic);
        }
    }

    private void startSubscribing(IManagedMqttClient client)
    {
        var group = groupFor(client);
        if (group.Subscribed) return;

        client.ApplicationMessageReceivedAsync += arg => ReceiveFromClientAsync(client, arg);

        // GH-5790. A held message can only be acknowledged on the connection it arrived on. Once that is gone
        // the broker resends it when the session resumes, so the held copy would only be a duplicate.
        client.DisconnectedAsync += _ =>
        {
            lock (group.HeldLock)
            {
                group.Held = new List<MqttApplicationMessageReceivedEventArgs>();
            }

            return Task.CompletedTask;
        };

        group.Subscribed = true;
    }

    internal Task ReceiveFromClientAsync(IManagedMqttClient client, MqttApplicationMessageReceivedEventArgs arg)
    {
        var topicName = arg.ApplicationMessage.Topic;
        if (tryFindListener(client, topicName, out var listener))
        {
            return listener.ReceiveAsync(arg);
        }

        if (!_listenersAreUp)
        {
            // GH-4802: the host is still starting, so this is a persistent session's backlog racing
            // listener registration rather than a topic nothing will ever listen to. Decline the
            // acknowledgement.
            arg.AutoAcknowledge = false;

            // GH-5790: and hold it for the listener. GH-4802 left it to the broker to redeliver, but a broker
            // resends an unacknowledged message only when the session resumes on a NEW connection, so the
            // message sat in flight, holding one of this client's in-flight slots, until the next reconnect.
            var group = groupFor(client);
            lock (group.HeldLock)
            {
                // The listener may have registered since the lookup above
                if (tryFindListener(client, topicName, out listener))
                {
                    listener.ReceiveHeldMessages([arg]);
                    return Task.CompletedTask;
                }

                if (!_listenersAreUp)
                {
                    if (group.Held.Count < MaximumHeldBeforeListeners)
                    {
                        group.Held.Add(arg);

                        _logger?.LogInformation(
                            "Received an MQTT message for topic {TopicName} before its listener was registered. Holding it unacknowledged until the listener registers",
                            topicName);
                    }
                    else
                    {
                        _logger?.LogWarning(
                            "Received an MQTT message for topic {TopicName} before its listener was registered, and this connection already holds {Count} such messages. Leaving it unacknowledged and not held: the broker will resend it only when the session resumes on a new connection",
                            topicName, group.Held.Count);
                    }

                    return Task.CompletedTask;
                }
            }
        }

        // Deliberately bounded to startup. An unacknowledged message occupies a slot in the broker's
        // in-flight window, so holding one indefinitely would eventually stall delivery for every topic
        // on this connection -- a worse failure than the drop it would replace. After startup the only
        // way to get here is a subscription the broker is holding from an earlier session for a topic
        // this process does not listen to, and that will never resolve.
        _logger?.LogWarning(
            "Received an MQTT message for topic {TopicName} that has no listener attached, and Wolverine has finished starting, so the message is being discarded. The broker is most likely still holding a subscription from an earlier session for a topic this process does not listen to",
            topicName);

        return Task.CompletedTask;
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

        // GH-4802: only a HIT is memoized. Caching the miss meant that one message arriving in the
        // startup window -- before SubscribeToTopicAsync had registered that topic's listener -- poisoned
        // the topic permanently: every later message for it resolved to the cached null and was discarded
        // for the life of the process. That turned a timing race into total, silent loss on one topic.
        if (listener is null) return false;

        group.TopicListeners = group.TopicListeners.AddOrUpdate(topicName, listener);

        return true;
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

        // GH-5770. A listener that restarts (after a pause, say) is a new MqttListener for the same topic on the
        // same connection. The stopped one used to stay first in line, so every later message for the topic went
        // to a listener whose receiver was latched or gone. Replace it, and take over the deferred messages it
        // handed back: they are still unacknowledged, and the broker will not redeliver them on this connection.
        var replaced = group.Listeners.Where(x => x.IsStopped && x.TopicName == listener.TopicName).ToArray();

        List<MqttApplicationMessageReceivedEventArgs> held;
        lock (group.HeldLock)
        {
            group.Listeners = group.Listeners.Except(replaced).Append(listener).ToArray();
            if (replaced.Length > 0)
            {
                group.TopicListeners = ImHashMap<string, MqttListener>.Empty;
            }

            // GH-5790: the messages that arrived for this listener before it registered
            held = group.Held.Where(x =>
                tryFindListener(client, x.ApplicationMessage.Topic, out var found) &&
                ReferenceEquals(found, listener)).ToList();
            if (held.Count > 0)
            {
                group.Held = group.Held.Except(held).ToList();
            }
        }

        if (held.Count > 0)
        {
            listener.ReceiveHeldMessages(held);
        }

        foreach (var previous in replaced)
        {
            listener.TakeOverHandedBackMessages(previous);
        }

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
