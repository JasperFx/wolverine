using Wolverine.Configuration;
using Wolverine.Runtime.Partitioning;

namespace Wolverine.Postgresql.Transport;

/// <summary>
/// Global partitioning topology where each slot is a PostgreSQL queue named
/// "baseName1", "baseName2", and so on. Each slot queue is backed by its own
/// wolverine_queue_{name} / wolverine_queue_{name}_scheduled table pair.
/// </summary>
public class PartitionedMessageTopologyWithDatabaseQueues : PartitionedMessageTopology<PostgresqlListenerConfiguration, PostgresqlSubscriberConfiguration>
{
    public PartitionedMessageTopologyWithDatabaseQueues(WolverineOptions options, PartitionSlots? listeningSlots,
        string baseName, int numberOfEndpoints) : base(options, listeningSlots, baseName, numberOfEndpoints)
    {
        MaxDegreeOfParallelism = PartitionSlots.Five;
    }

    /// <summary>
    /// Build a publish-only topology: the shard queues are provisioned and published to, but this
    /// node never listens to them, so another application can consume them.
    /// </summary>
    public PartitionedMessageTopologyWithDatabaseQueues(WolverineOptions options, string baseName,
        int numberOfEndpoints, bool publishOnly) : base(options, publishOnly ? null : PartitionSlots.Five, baseName,
        numberOfEndpoints)
    {
        PublishOnly = publishOnly;
        if (!publishOnly) MaxDegreeOfParallelism = PartitionSlots.Five;
    }

    protected override Endpoint buildEndpoint(WolverineOptions options, string name)
    {
        var transport = options.PostgresqlTransport();
        return transport.Queues[transport.MaybeCorrectName(name)];
    }

    protected override PostgresqlListenerConfiguration buildListener(WolverineOptions options, string name)
    {
        return options.ListenToPostgresqlQueue(name);
    }

    protected override PostgresqlSubscriberConfiguration buildSubscriber(IPublishToExpression expression, string name)
    {
        return expression.ToPostgresqlQueue(name);
    }
}
