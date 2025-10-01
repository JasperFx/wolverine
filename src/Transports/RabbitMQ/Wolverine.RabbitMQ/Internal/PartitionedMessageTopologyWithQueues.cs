using Wolverine.Configuration;
using Wolverine.Runtime.Partitioning;

namespace Wolverine.RabbitMQ.Internal;

public class PartitionedMessageTopologyWithQueues : PartitionedMessageTopology<RabbitMqListenerConfiguration, RabbitMqSubscriberConfiguration>
{
    public PartitionedMessageTopologyWithQueues(WolverineOptions options, PartitionSlots? listeningSlots, string baseName, int numberOfEndpoints) : base(options, listeningSlots, baseName, numberOfEndpoints)
    {
        MaxDegreeOfParallelism = PartitionSlots.Five;
    }

    protected override Endpoint buildEndpoint(WolverineOptions options, string name)
    {
        return options.RabbitMqTransport().Queues[name];
    }

    protected override RabbitMqListenerConfiguration buildListener(WolverineOptions options, string name)
    {
        return options.ListenToRabbitQueue(name);
    }

    protected override RabbitMqSubscriberConfiguration buildSubscriber(IPublishToExpression expression, string name)
    {
        return expression.ToRabbitQueue(name);
    }
}