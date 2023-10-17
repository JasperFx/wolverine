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

namespace Wolverine.MQTT.Internals;

public class MqttTransport : TransportBase<MqttTopic>, IAsyncDisposable
{
    public LightweightCache<string, MqttTopic> Topics { get; } = new();
    private ImHashMap<string, MqttListener> _listeners = ImHashMap<string, MqttListener>.Empty;
    private bool _subscribed;
    private ILogger<MqttTransport> _logger;

    public static string TopicForUri(Uri uri)
    {
        return uri.LocalPath.Trim('/');
    }
    
    public MqttTransport() : base("mqtt", "MQTT Transport")
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
        topics.Retain = true;
        topics.OutgoingRules.Add(new TopicRoutingRule()); // this will make any messages use the auto resolved topic name
        
        _logger = runtime.LoggerFactory.CreateLogger<MqttTransport>();

        var mqttFactory = new MqttFactory();
            
        // TODO -- add logging here!
        Client = mqttFactory.CreateManagedMqttClient();

        Options.ClientOptions.ProtocolVersion = MqttProtocolVersion.V500;

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
        if (_listeners.TryFind(topicName, out var listener))
        {
            return listener.ReceiveAsync(arg);
        }
        else
        {
            _logger?.LogInformation("Received MQTT message for topic {TopicName} that has no listener attached", topicName);
            return Task.CompletedTask;
        }
    }

    internal IManagedMqttClient Client { get; private set; }

    public ManagedMqttClientOptions Options { get; set; } = new ManagedMqttClientOptions
        { ClientOptions = new MqttClientOptions() };

    public override Endpoint ReplyEndpoint()
    {
        return Topics[ResponseTopic];
    }

    public async ValueTask DisposeAsync()
    {
        await Client.StopAsync();
    }

    internal async ValueTask SubscribeToTopicAsync(string topicName, MqttListener listener, MqttTopic mqttTopic)
    {
        _listeners = _listeners.AddOrUpdate(topicName, listener);

        await Client.SubscribeAsync(topicName, mqttTopic.QualityOfServiceLevel);

        startSubscribing();
    }
}