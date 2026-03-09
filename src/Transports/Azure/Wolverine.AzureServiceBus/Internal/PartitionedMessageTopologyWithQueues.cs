using Wolverine.Configuration;
using Wolverine.Runtime.Partitioning;

namespace Wolverine.AzureServiceBus.Internal;

public class PartitionedMessageTopologyWithQueues : PartitionedMessageTopology<AzureServiceBusQueueListenerConfiguration, AzureServiceBusQueueSubscriberConfiguration>
{
    public PartitionedMessageTopologyWithQueues(WolverineOptions options, PartitionSlots? listeningSlots, string baseName, int numberOfEndpoints) : base(options, listeningSlots, baseName, numberOfEndpoints)
    {
        MaxDegreeOfParallelism = PartitionSlots.Five;
    }

    protected override Endpoint buildEndpoint(WolverineOptions options, string name)
    {
        return options.AzureServiceBusTransport().Queues[name];
    }

    protected override AzureServiceBusQueueListenerConfiguration buildListener(WolverineOptions options, string name)
    {
        return options.ListenToAzureServiceBusQueue(name);
    }

    protected override AzureServiceBusQueueSubscriberConfiguration buildSubscriber(IPublishToExpression expression, string name)
    {
        return expression.ToAzureServiceBusQueue(name);
    }
}
