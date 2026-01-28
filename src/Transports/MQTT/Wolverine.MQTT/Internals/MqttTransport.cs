using ImTools;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Formatter;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;
using Timer = System.Timers.Timer;

namespace Wolverine.MQTT.Internals;

public class MqttTransport : TransportBase<MqttTopic>, IAsyncDisposable
{
    public LightweightCache<string, MqttTopic> Topics { get; } = new();
    private List<MqttListener> _listeners = new();
    private ImHashMap<string, MqttListener> _topicListeners = ImHashMap<string, MqttListener>.Empty;
    private bool _subscribed;
    private ILogger<MqttTransport> _logger;
    private Timer? _jwtTokenRefreshTimer;

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
        ResponseTopic = "wolverine/response/" + runtime.Options.Durability.AssignedNodeNumber;
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
    
    private Task onClientConnected(MqttClientConnectedEventArgs arg)
    {
        if (arg.ConnectResult.ResultCode != MqttClientConnectResultCode.Success)
        {
            return Task.CompletedTask;
        }

        if (JwtAuthenticationOptions == null)
        {
            return Task.CompletedTask;
        }

        _jwtTokenRefreshTimer = new Timer(JwtAuthenticationOptions.RefreshPeriod);
        _jwtTokenRefreshTimer.Elapsed += async (sender, args) => await RefreshToken(sender, args);
        _jwtTokenRefreshTimer.Start();
        return Task.CompletedTask;
        
        async Task RefreshToken(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (Client.IsConnected)
            {
                await Client.InternalClient.SendExtendedAuthenticationExchangeDataAsync(
                    new MqttExtendedAuthenticationExchangeData()
                    {
                        AuthenticationData = await JwtAuthenticationOptions!.GetTokenCallBack(),
                        ReasonCode = MQTTnet.Protocol.MqttAuthenticateReasonCode.ReAuthenticate
                    });
            }
        }
    }

    private Task onClientDisconnected(MqttClientDisconnectedEventArgs arg)
    {
        _jwtTokenRefreshTimer?.Stop();
        _jwtTokenRefreshTimer?.Dispose();
        return Task.CompletedTask;
    }

    internal bool tryFindListener(string topicName, out MqttListener listener)
    {
        if (_topicListeners.TryFind(topicName, out listener))
        {
            return listener is not null;
        }

        listener = _listeners.FirstOrDefault(x => x.TopicName == topicName) ?? _listeners.FirstOrDefault(x =>
            MqttTopicFilterComparer.Compare(topicName, x.TopicName) == MqttTopicFilterCompareResult.IsMatch);

        _topicListeners = _topicListeners.AddOrUpdate(topicName, listener);


        return listener is not null;
    }

    internal IManagedMqttClient Client { get; private set; }
    internal MqttJwtAuthenticationOptions? JwtAuthenticationOptions { get; set; }

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
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (Client is not null)
                await Client.StopAsync();
            _jwtTokenRefreshTimer?.Dispose();
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