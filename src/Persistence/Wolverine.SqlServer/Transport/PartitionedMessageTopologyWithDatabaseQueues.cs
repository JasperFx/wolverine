using Wolverine.Configuration;
using Wolverine.Runtime.Partitioning;

namespace Wolverine.SqlServer.Transport;

/// <summary>
/// Global partitioning topology where each slot is a SQL Server queue named
/// "baseName1", "baseName2", and so on. Each slot queue is backed by its own
/// wolverine_queue_{name} / wolverine_queue_{name}_scheduled table pair.
/// </summary>
public class PartitionedMessageTopologyWithDatabaseQueues : PartitionedMessageTopology<SqlServerListenerConfiguration, SqlServerSubscriberConfiguration>
{
    private bool _optimizeThroughput;

    public PartitionedMessageTopologyWithDatabaseQueues(WolverineOptions options, PartitionSlots? listeningSlots,
        string baseName, int numberOfEndpoints) : base(options, listeningSlots, baseName, numberOfEndpoints)
    {
        MaxDegreeOfParallelism = PartitionSlots.Five;

        // Partitioned processing is exactly the ordered-throughput case the seq-clustered layout
        // was built for, so opt the shard queues in by default regardless of the transport-wide
        // setting. Set this back to false if the shard tables have to match the default layout.
        OptimizeThroughput = true;
    }

    /// <summary>
    ///     Use the higher-throughput, seq-clustered table layout for the shard queues. Defaults to
    ///     true for a sharded topology -- see <see cref="SqlServerQueue.OptimizeThroughput" />.
    /// </summary>
    public bool OptimizeThroughput
    {
        get => _optimizeThroughput;
        set
        {
            _optimizeThroughput = value;
            foreach (var queue in Slots.OfType<SqlServerQueue>())
            {
                queue.OptimizeThroughput = value;
            }
        }
    }

    protected override Endpoint buildEndpoint(WolverineOptions options, string name)
    {
        var transport = options.SqlServerTransport();
        return transport.Queues[transport.MaybeCorrectName(name)];
    }

    protected override SqlServerListenerConfiguration buildListener(WolverineOptions options, string name)
    {
        return options.ListenToSqlServerQueue(name);
    }

    protected override SqlServerSubscriberConfiguration buildSubscriber(IPublishToExpression expression, string name)
    {
        return expression.ToSqlServerQueue(name);
    }
}
