using Wolverine.Configuration;

namespace Wolverine.Kafka;

public class KafkaSubscriberConfiguration : SubscriberConfiguration<KafkaSubscriberConfiguration, KafkaTopic>
{
    internal KafkaSubscriberConfiguration(KafkaTopic endpoint) : base(endpoint)
    {
    }
    
}