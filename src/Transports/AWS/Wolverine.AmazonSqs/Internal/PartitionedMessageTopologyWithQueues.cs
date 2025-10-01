using Wolverine.Configuration;
using Wolverine.Runtime.Partitioning;

namespace Wolverine.AmazonSqs.Internal;

public class PartitionedMessageTopologyWithQueues : PartitionedMessageTopology<AmazonSqsListenerConfiguration, AmazonSqsSubscriberConfiguration>
{
    public PartitionedMessageTopologyWithQueues(WolverineOptions options, PartitionSlots? listeningSlots, string baseName, int numberOfEndpoints) : base(options, listeningSlots, baseName, numberOfEndpoints)
    {
        MaxDegreeOfParallelism = PartitionSlots.Five;
    }

    protected override Endpoint buildEndpoint(WolverineOptions options, string name)
    {
        return options.AmazonSqsTransport().Queues[name];
    }

    protected override AmazonSqsListenerConfiguration buildListener(WolverineOptions options, string name)
    {
        return options.ListenToSqsQueue(name);
    }

    protected override AmazonSqsSubscriberConfiguration buildSubscriber(IPublishToExpression expression, string name)
    {
        return expression.ToSqsQueue(name);
    }
}