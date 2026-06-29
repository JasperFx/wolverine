using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.SqlServer.Transport;

public class SqlServerPersistenceExpression : BrokerExpression<SqlServerTransport, SqlServerQueue, SqlServerQueue, SqlServerListenerConfiguration, SqlServerSubscriberConfiguration, SqlServerPersistenceExpression>
{
    private readonly WolverineOptions _options;

    public SqlServerPersistenceExpression(SqlServerTransport transport, WolverineOptions options) : base(transport, options)
    {
        _options = options;
    }

    protected override SqlServerListenerConfiguration createListenerExpression(SqlServerQueue listenerEndpoint)
    {
        return new SqlServerListenerConfiguration(listenerEndpoint);
    }

    protected override SqlServerSubscriberConfiguration createSubscriberExpression(SqlServerQueue subscriberEndpoint)
    {
        return new SqlServerSubscriberConfiguration(subscriberEndpoint);
    }

    /// <summary>
    /// Opt into a higher-throughput storage layout for the Sql Server queue and scheduled message
    /// tables. The tables are clustered on a monotonic identity column (FIFO dequeue + contiguous
    /// deletes) with a unique non-clustered index on the message id, rather than a clustered primary
    /// key on a random Guid. This dramatically improves dequeue throughput and tail latency on
    /// non-trivial queue depths, at the cost of a one-time table rebuild when first enabled on an
    /// existing database. New applications should turn this on; existing applications should enable
    /// it during a maintenance window (ideally with queues drained).
    /// </summary>
    public SqlServerPersistenceExpression OptimizeQueueThroughput()
    {
        Transport.OptimizeQueueThroughput = true;
        return this;
    }

    /// <summary>
    /// Disable inbox and outbox usage on all Sql Server Transport endpoints
    /// </summary>
    /// <returns></returns>
    public SqlServerPersistenceExpression DisableInboxAndOutboxOnAll()
    {
        var policy = new LambdaEndpointPolicy<SqlServerQueue>((e, _) =>
        {
            if (e.Role == EndpointRole.System)
            {
                return;
            }

            e.Mode = EndpointMode.BufferedInMemory;
        });

        _options.Policies.Add(policy);
        return this;
    }
}