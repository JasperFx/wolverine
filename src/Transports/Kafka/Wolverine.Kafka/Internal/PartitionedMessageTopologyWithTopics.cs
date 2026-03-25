using Confluent.Kafka;
using Wolverine.Configuration;
using Wolverine.Runtime.Partitioning;

namespace Wolverine.Kafka.Internal;

public class PartitionedMessageTopologyWithTopics : PartitionedMessageTopology<KafkaListenerConfiguration, KafkaSubscriberConfiguration>
{
    private readonly string _baseName;

    public PartitionedMessageTopologyWithTopics(WolverineOptions options, PartitionSlots? listeningSlots, string baseName, int numberOfEndpoints) : base(options, listeningSlots, baseName, numberOfEndpoints)
    {
        _baseName = baseName;
        MaxDegreeOfParallelism = PartitionSlots.Five;
    }

    protected override Endpoint buildEndpoint(WolverineOptions options, string name)
    {
        return options.KafkaTransport().Topics[name];
    }

    protected override KafkaListenerConfiguration buildListener(WolverineOptions options, string name)
    {
        var listener = options.ListenToKafkaTopic(name);

        // All nodes sharing these sharded topics MUST use the same Kafka consumer
        // group so that Kafka assigns partitions exclusively to one consumer per
        // partition, preventing duplicate message delivery across nodes.
        // Use the baseName as the shared group id so it is deterministic and
        // independent of ServiceName.
        listener.ConfigureConsumer(config =>
        {
            config.BootstrapServers = options.KafkaTransport().ConsumerConfig.BootstrapServers;
            config.GroupId = _baseName;
        });

        // Don't overwrite the envelope's GroupId with the Kafka consumer group name.
        // The GroupId on the envelope is the business partition key (e.g. aggregate id)
        // set by ByPropertyNamed, and must be preserved for correct sharding on the
        // companion local queue.
        listener.DisableConsumerGroupIdStamping();

        return listener;
    }

    protected override KafkaSubscriberConfiguration buildSubscriber(IPublishToExpression expression, string name)
    {
        return expression.ToKafkaTopic(name);
    }
}
