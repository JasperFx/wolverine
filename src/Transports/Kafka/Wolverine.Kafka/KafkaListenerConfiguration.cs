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


}