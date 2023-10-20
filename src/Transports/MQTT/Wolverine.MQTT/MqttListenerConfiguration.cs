using MQTTnet.Protocol;
using Wolverine.Configuration;

namespace Wolverine.MQTT;

public class MqttListenerConfiguration : ListenerConfiguration<MqttListenerConfiguration, MqttTopic>
{
    public MqttListenerConfiguration(MqttTopic endpoint) : base(endpoint)
    {
    }

    public MqttListenerConfiguration(Func<MqttTopic> source) : base(source)
    {
    }

    /// <summary>
    /// Messages should be retained by the MQTT broker even when there is not
    /// an active client
    /// </summary>
    /// <returns></returns>
    public MqttListenerConfiguration RetainMessages()
    {
        add(e => e.Retain = true);
        return this;
    }

    /// <summary>
    /// Override the quality of service for just this endpoint. Default is AtLeastOnce
    /// </summary>
    /// <param name="serviceLevel"></param>
    /// <returns></returns>
    public MqttListenerConfiguration QualityOfService(MqttQualityOfServiceLevel serviceLevel)
    {
        add(e => e.QualityOfServiceLevel = serviceLevel);
        return this;
    }
    
    /// <summary>
    /// Use a custom interoperability strategy to map Wolverine messages to an upstream
    /// system's protocol
    /// </summary>
    /// <param name="mapper"></param>
    /// <returns></returns>
    public MqttListenerConfiguration UseInterop(IMqttEnvelopeMapper mapper)
    {
        add(e => e.EnvelopeMapper = mapper);
        return this;
    }
}