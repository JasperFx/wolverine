using Wolverine.Configuration;
using Wolverine.Runtime.Partitioning;

namespace Wolverine.Pubsub.Internal;

public class PartitionedMessageTopologyWithTopics : PartitionedMessageTopology<PubsubTopicListenerConfiguration, PubsubTopicSubscriberConfiguration>
{
    public PartitionedMessageTopologyWithTopics(WolverineOptions options, PartitionSlots? listeningSlots, string baseName, int numberOfEndpoints) : base(options, listeningSlots, baseName, numberOfEndpoints)
    {
        MaxDegreeOfParallelism = PartitionSlots.Five;
    }

    /// <summary>
    /// Build the sharded topics against a named broker registered through
    /// <c>AddNamedPubsubBroker()</c> rather than the default, unnamed transport.
    /// </summary>
    public PartitionedMessageTopologyWithTopics(WolverineOptions options, PartitionSlots? listeningSlots, string baseName, int numberOfEndpoints, BrokerName? brokerName) : base(options, listeningSlots, baseName, numberOfEndpoints, brokerName)
    {
        MaxDegreeOfParallelism = PartitionSlots.Five;
    }

    protected override Endpoint buildEndpoint(WolverineOptions options, string name)
    {
        var transport = options.PubsubTransport(_brokerName);
        return transport.Topics[transport.MaybeCorrectName(name)];
    }

    protected override PubsubTopicListenerConfiguration buildListener(WolverineOptions options, string name)
    {
        return _brokerName is null
            ? options.ListenToPubsubTopic(name)
            : options.ListenToPubsubTopicOnNamedBroker(_brokerName, name);
    }

    protected override PubsubTopicSubscriberConfiguration buildSubscriber(IPublishToExpression expression, string name)
    {
        return _brokerName is null
            ? expression.ToPubsubTopic(name)
            : expression.ToPubsubTopicOnNamedBroker(_brokerName, name);
    }
}
