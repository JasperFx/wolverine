using ImTools;
using JasperFx.Core;
using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Formatter;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;

namespace Wolverine.MQTT.Internals;

public class MqttTransport : TransportBase<MqttTopic>, IAsyncDisposable
{
    [IgnoreDescription]
    public LightweightCache<string, MqttTopic> Topics { get; } = new();
    private List<MqttListener> _listeners = new();
    private ImHashMap<string, MqttListener> _topicListeners = ImHashMap<string, MqttListener>.Empty;
    private bool _subscribed;
    private ILogger<MqttTransport> _logger = null!;
    private CancellationTokenSource? _refreshCts;

    public static string TopicForUri(Uri uri)
    {
        if (uri == null) return string.Empty;
        return uri.LocalPath.Trim('/');
    }

    public MqttTransport() : base("mqtt", "MQTT Transport", ["mqtt"])
    {
        Topics.OnMissing = topicName => new MqttTopic(topicName, this, EndpointRole.Application);
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

        var mqttFactory = new MqttFactory();

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
        foreach (var endpoint in Topics)
        {
            endpoint.Compile(runtime);
        }
    }

    public WolverineTransportHealthCheck BuildHealthCheck(IWolverineRuntime runtime)
    {
        return new MqttHealthCheck(this);
    }

    private void startSubscribing()
    {
        if (_subscribed) return;

        Client.ApplicationMessageReceivedAsync += receiveAsync;
        _subscribed = true;
    }

    private Task receiveAsync(MqttApplicationMessageReceivedEventArgs arg)
    {
        var topicName = arg.ApplicationMessage.Topic;
        if (tryFindListener(topicName, out var listener))
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
                        .SendExtendedAuthenticationExchangeDataAsync(
                            new MqttExtendedAuthenticationExchangeData
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

    internal bool tryFindListener(string topicName, out MqttListener listener)
    {
        if (_topicListeners.TryFind(topicName, out listener))
        {
            return listener is not null;
        }

        listener = (_listeners.FirstOrDefault(x => x.TopicName == topicName) ?? _listeners.FirstOrDefault(x =>
            MqttTopicFilterComparer.Compare(topicName, x.TopicName) == MqttTopicFilterCompareResult.IsMatch))!;

        _topicListeners = _topicListeners.AddOrUpdate(topicName, listener!);


        return listener is not null;
    }

    internal IManagedMqttClient Client { get; set; } = null!;
    internal MqttJwtAuthenticationOptions? JwtAuthenticationOptions { get; set; }

    [ChildDescription]
    public ManagedMqttClientOptions Options { get; set; } = new ManagedMqttClientOptions
        { ClientOptions = new MqttClientOptions() };

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
            if (Client is not null)
                await Client.StopAsync();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal async ValueTask SubscribeToTopicAsync(string topicName, MqttListener listener, MqttTopic mqttTopic)
    {
        _listeners.Add(listener);

        await Client.SubscribeAsync(topicName, mqttTopic.QualityOfServiceLevel);

        startSubscribing();
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