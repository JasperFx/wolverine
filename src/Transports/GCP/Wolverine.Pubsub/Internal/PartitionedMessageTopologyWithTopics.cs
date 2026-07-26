using Wolverine.Configuration;
using Wolverine.Runtime.Partitioning;

namespace Wolverine.Pubsub.Internal;

public class PartitionedMessageTopologyWithTopics : PartitionedMessageTopology<PubsubTopicListenerConfiguration, PubsubTopicSubscriberConfiguration>
{
    public PartitionedMessageTopologyWithTopics(WolverineOptions options, PartitionSlots? listeningSlots, string baseName, int numberOfEndpoints, BrokerName? brokerName = null) : base(options, listeningSlots, baseName, numberOfEndpoints, brokerName)
    {
        MaxDegreeOfParallelism = PartitionSlots.Five;
    }

    protected override Endpoint buildEndpoint(WolverineOptions options, string name)
    {
        var transport = options.PubsubTransport(BrokerName);
        return transport.Topics[transport.MaybeCorrectName(name)];
    }

    protected override PubsubTopicListenerConfiguration buildListener(WolverineOptions options, string name)
    {
        return BrokerName is null
            ? options.ListenToPubsubTopic(name)
            : options.ListenToPubsubTopicOnNamedBroker(BrokerName, name);
    }

    protected override PubsubTopicSubscriberConfiguration buildSubscriber(IPublishToExpression expression, string name)
    {
        return BrokerName is null
            ? expression.ToPubsubTopic(name)
            : expression.ToPubsubTopicOnNamedBroker(BrokerName, name);
    }
}
