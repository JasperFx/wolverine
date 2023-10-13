using MQTTnet.Client;

namespace Wolverine.MQTT;

public class MqttEnvelope : Envelope
{
    public MqttApplicationMessageReceivedEventArgs Args { get; }
    public bool IsAcked { get; set; }
    
    public bool ShouldRequeue { get; set; }

    internal MqttEnvelope(MqttTopic topic, MqttApplicationMessageReceivedEventArgs args) 
    {
        Args = args;
        Data = args.ApplicationMessage.PayloadSegment.Array;
        MessageType = topic.MessageTypeName;
    }
}