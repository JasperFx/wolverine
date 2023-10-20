using Wolverine.Configuration;

namespace Wolverine.Kafka;

public class KafkaSubscriberConfiguration : SubscriberConfiguration<KafkaSubscriberConfiguration, KafkaTopic>
{
    internal KafkaSubscriberConfiguration(KafkaTopic endpoint) : base(endpoint)
    {
    }
    
    /// <summary>
    /// Use a custom interoperability strategy to map Wolverine messages to an upstream
    /// system's protocol
    /// </summary>
    /// <param name="mapper"></param>
    /// <returns></returns>
    public KafkaSubscriberConfiguration UseInterop(IKafkaEnvelopeMapper mapper)
    {
        add(e => e.Mapper = mapper);
        return this;
    }
}