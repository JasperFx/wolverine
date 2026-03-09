using Wolverine.Configuration;
using Wolverine.Runtime.Partitioning;

namespace Wolverine.Pubsub.Internal;

public class PartitionedMessageTopologyWithTopics : PartitionedMessageTopology<PubsubTopicListenerConfiguration, PubsubTopicSubscriberConfiguration>
{
    public PartitionedMessageTopologyWithTopics(WolverineOptions options, PartitionSlots? listeningSlots, string baseName, int numberOfEndpoints) : base(options, listeningSlots, baseName, numberOfEndpoints)
    {
        MaxDegreeOfParallelism = PartitionSlots.Five;
    }

    protected override Endpoint buildEndpoint(WolverineOptions options, string name)
    {
        return options.PubsubTransport().Topics[name];
    }

    protected override PubsubTopicListenerConfiguration buildListener(WolverineOptions options, string name)
    {
        return options.ListenToPubsubTopic(name);
    }

    protected override PubsubTopicSubscriberConfiguration buildSubscriber(IPublishToExpression expression, string name)
    {
        return expression.ToPubsubTopic(name);
    }
}
