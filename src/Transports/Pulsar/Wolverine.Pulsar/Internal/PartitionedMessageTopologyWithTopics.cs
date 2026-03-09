using Wolverine.Configuration;
using Wolverine.Runtime.Partitioning;

namespace Wolverine.Pulsar.Internal;

public class PartitionedMessageTopologyWithTopics : PartitionedMessageTopology<PulsarListenerConfiguration, PulsarSubscriberConfiguration>
{
    public PartitionedMessageTopologyWithTopics(WolverineOptions options, PartitionSlots? listeningSlots, string baseName, int numberOfEndpoints) : base(options, listeningSlots, baseName, numberOfEndpoints)
    {
        MaxDegreeOfParallelism = PartitionSlots.Five;
    }

    protected override Endpoint buildEndpoint(WolverineOptions options, string name)
    {
        return options.PulsarTransport().EndpointFor(name);
    }

    protected override PulsarListenerConfiguration buildListener(WolverineOptions options, string name)
    {
        return options.ListenToPulsarTopic(name);
    }

    protected override PulsarSubscriberConfiguration buildSubscriber(IPublishToExpression expression, string name)
    {
        return expression.ToPulsarTopic(name);
    }
}
