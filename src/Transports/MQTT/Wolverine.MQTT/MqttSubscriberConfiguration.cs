using MQTTnet.Protocol;
using Wolverine.Configuration;

namespace Wolverine.MQTT;

public class MqttSubscriberConfiguration : SubscriberConfiguration<MqttSubscriberConfiguration, MqttTopic>
{
    internal MqttSubscriberConfiguration(MqttTopic endpoint) : base(endpoint)
    {
    }
    
    /// <summary>
    /// Messages should be retained by the MQTT broker even when there is not
    /// an active client
    /// </summary>
    /// <returns></returns>
    public MqttSubscriberConfiguration RetainMessages()
    {
        add(e => e.Retain = true);
        return this;
    }
    
    /// <summary>
    /// Override the quality of service for just this endpoint. Default is AtLeastOnce
    /// </summary>
    /// <param name="serviceLevel"></param>
    /// <returns></returns>
    public MqttSubscriberConfiguration QualityOfService(MqttQualityOfServiceLevel serviceLevel)
    {
        add(e => e.QualityOfServiceLevel = serviceLevel);
        return this;
    }
}