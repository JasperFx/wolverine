using Wolverine.MQTT.Internals;

namespace Wolverine.MQTT;

public class MqttTransportExpression
{
    private readonly MqttTransport _transport;

    internal MqttTransportExpression(MqttTransport transport)
    {
        _transport = transport;
    }
}