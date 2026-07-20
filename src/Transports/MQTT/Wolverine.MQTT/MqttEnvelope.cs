using System.Buffers;
using MQTTnet;

namespace Wolverine.MQTT;

public class MqttEnvelope : Envelope
{
    public MqttApplicationMessageReceivedEventArgs Args { get; }
    public bool IsAcked { get; set; }

    public bool ShouldRequeue { get; set; }

    internal MqttEnvelope(MqttTopic topic, MqttApplicationMessageReceivedEventArgs args)
    {
        Args = args;
        Data = args.ApplicationMessage.Payload.ToArray();
        MessageType = topic.MessageTypeName;
        Destination = topic.Uri;
    }
}
