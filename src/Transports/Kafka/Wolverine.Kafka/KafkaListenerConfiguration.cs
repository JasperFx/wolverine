using Wolverine.Configuration;

namespace Wolverine.Kafka;

public class KafkaListenerConfiguration : ListenerConfiguration<KafkaListenerConfiguration, KafkaTopic>
{
    public KafkaListenerConfiguration(KafkaTopic endpoint) : base(endpoint)
    {
    }

    public KafkaListenerConfiguration(Func<KafkaTopic> source) : base(source)
    {
    }

    /// <summary>
    /// Use a custom interoperability strategy to map Wolverine messages to an upstream
    /// system's protocol
    /// </summary>
    /// <param name="mapper"></param>
    /// <returns></returns>
    public KafkaListenerConfiguration UseInterop(IKafkaEnvelopeMapper mapper)
    {
        add(e => e.Mapper = mapper);
        return this;
    }
}