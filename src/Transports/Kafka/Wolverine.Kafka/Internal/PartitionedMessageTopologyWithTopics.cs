using Wolverine.Configuration;
using Wolverine.Runtime.Partitioning;

namespace Wolverine.Kafka.Internal;

public class PartitionedMessageTopologyWithTopics : PartitionedMessageTopology<KafkaListenerConfiguration, KafkaSubscriberConfiguration>
{
    public PartitionedMessageTopologyWithTopics(WolverineOptions options, PartitionSlots? listeningSlots, string baseName, int numberOfEndpoints) : base(options, listeningSlots, baseName, numberOfEndpoints)
    {
        MaxDegreeOfParallelism = PartitionSlots.Five;
    }

    protected override Endpoint buildEndpoint(WolverineOptions options, string name)
    {
        return options.KafkaTransport().Topics[name];
    }

    protected override KafkaListenerConfiguration buildListener(WolverineOptions options, string name)
    {
        return options.ListenToKafkaTopic(name);
    }

    protected override KafkaSubscriberConfiguration buildSubscriber(IPublishToExpression expression, string name)
    {
        return expression.ToKafkaTopic(name);
    }
}
