using Wolverine.Configuration;
using Wolverine.Nats.Configuration;
using Wolverine.Runtime.Partitioning;

namespace Wolverine.Nats.Internal;

public class PartitionedMessageTopologyWithSubjects : PartitionedMessageTopology<NatsListenerConfiguration, NatsSubscriberConfiguration>
{
    public PartitionedMessageTopologyWithSubjects(WolverineOptions options, PartitionSlots? listeningSlots, string baseName, int numberOfEndpoints) : base(options, listeningSlots, baseName, numberOfEndpoints)
    {
        MaxDegreeOfParallelism = PartitionSlots.Five;
    }

    protected override Endpoint buildEndpoint(WolverineOptions options, string name)
    {
        return options.NatsTransport().EndpointForSubject(name);
    }

    protected override NatsListenerConfiguration buildListener(WolverineOptions options, string name)
    {
        return options.ListenToNatsSubject(name);
    }

    protected override NatsSubscriberConfiguration buildSubscriber(IPublishToExpression expression, string name)
    {
        return expression.ToNatsSubject(name);
    }
}
