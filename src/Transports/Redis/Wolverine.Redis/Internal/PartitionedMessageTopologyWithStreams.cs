using Wolverine.Configuration;
using Wolverine.Runtime.Partitioning;

namespace Wolverine.Redis.Internal;

public class PartitionedMessageTopologyWithStreams : PartitionedMessageTopology<RedisListenerConfiguration, RedisSubscriberConfiguration>
{
    public PartitionedMessageTopologyWithStreams(WolverineOptions options, PartitionSlots? listeningSlots, string baseName, int numberOfEndpoints) : base(options, listeningSlots, baseName, numberOfEndpoints)
    {
        MaxDegreeOfParallelism = PartitionSlots.Five;
    }

    protected override Endpoint buildEndpoint(WolverineOptions options, string name)
    {
        return options.Transports.GetOrCreate<RedisTransport>().StreamEndpoint(name, 0);
    }

    protected override RedisListenerConfiguration buildListener(WolverineOptions options, string name)
    {
        return options.ListenToRedisStream(name, "wolverine", 0);
    }

    protected override RedisSubscriberConfiguration buildSubscriber(IPublishToExpression expression, string name)
    {
        return expression.ToRedisStream(name, 0);
    }
}
