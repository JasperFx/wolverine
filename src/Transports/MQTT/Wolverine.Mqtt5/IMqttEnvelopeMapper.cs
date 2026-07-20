using MQTTnet;
using Wolverine.Transports;

namespace Wolverine.MQTT;

public interface IMqttEnvelopeMapper : IEnvelopeMapper<MqttApplicationMessage, MqttApplicationMessage>;