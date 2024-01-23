using System.Text;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Protocol;
using Wolverine.Configuration;
using Wolverine.MQTT.Internals;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util;

namespace Wolverine.MQTT;

public class MqttTopic : Endpoint, ISender
{
    public const string WolverineTopicsName = "wolverine/topics";
    
    public string TopicName { get; }
    private CancellationToken _cancellation;

    public MqttTopic(string topicName, MqttTransport parent, EndpointRole role) : base(new Uri("mqtt://topic/" + topicName.Trim('/')), role)
    {
        TopicName = topicName.Trim('/');
        Parent = parent;

        EndpointName = topicName;

        EnvelopeMapper = new MqttEnvelopeMapper(this);
        Mode = EndpointMode.BufferedInMemory;
    }

    public MqttTransport Parent { get; }

    /// <summary>
    /// When set, overrides the built in envelope mapping with a custom
    /// implementation
    /// </summary>
    public IMqttEnvelopeMapper EnvelopeMapper { get; set; }

    public override bool AutoStartSendingAgent()
    {
        return base.AutoStartSendingAgent() || RoutingType == RoutingMode.ByTopic;
    }

    public override async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        _cancellation = runtime.Cancellation;
        
        MessageTypeName = MessageType?.ToMessageTypeName();

        var logger = runtime.LoggerFactory.CreateLogger<MqttListener>();
        
        var listener = new MqttListener(Parent, logger, this, receiver);
        await Parent.SubscribeToTopicAsync(TopicName, listener, this);

        return listener;
    }

    public string? MessageTypeName { get; private set; }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        return this;
    }

    bool ISender.SupportsNativeScheduledSend => false;
    Uri ISender.Destination => Uri;
    public async Task<bool> PingAsync()
    {
        try
        {
            await Parent.Client.PingAsync(_cancellation);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal ManagedMqttApplicationMessage BuildMessage(Envelope envelope)
    {
        var appMessage = new MqttApplicationMessage();
        EnvelopeMapper.MapEnvelopeToOutgoing(envelope, appMessage);
        
        var message = new ManagedMqttApplicationMessage
        {
            ApplicationMessage = appMessage,
            Id = Guid.NewGuid(),
        };

        return message;
    }

    public ValueTask SendAsync(Envelope envelope)
    {
        var message = BuildMessage(envelope);
        return new ValueTask(Parent.Client.EnqueueAsync(message));
    }
    
    public MqttQualityOfServiceLevel QualityOfServiceLevel { get; set; } = MqttQualityOfServiceLevel.AtLeastOnce;

    public bool Retain { get; set; } = false;




}