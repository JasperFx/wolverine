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
    /// Use the higher-throughput storage layout for the Sql Server queue and scheduled message
    /// tables, and move existing tables to it. The tables are clustered on a monotonic identity column (FIFO
    /// dequeue + contiguous deletes) with a unique non-clustered index on the message id, rather than a clustered
    /// primary key on a random Guid. This layout is the default from 7.0 for the queue tables Wolverine creates
    /// (GH-5192); call this only to rebuild existing classic tables too, which takes an exclusive lock on each
    /// table, so drain the queues and do it in a maintenance window.
    /// </summary>
    public SqlServerPersistenceExpression OptimizeQueueThroughput()
    {
        Transport.OptimizeQueueThroughput = true;
        return this;
    }

    /// <summary>
    /// GH-5192. Use the classic storage layout of Wolverine 6.x (a clustered primary key on the message id and an
    /// index on the timestamp) for the queue tables Wolverine creates, and move existing tables on the
    /// <see cref="OptimizeQueueThroughput" /> layout back to it. Moving a table rebuilds it under an exclusive
    /// lock, so drain the queues first.
    /// </summary>
    public SqlServerPersistenceExpression UseClassicQueueLayout()
    {
        Transport.OptimizeQueueThroughput = false;
        return this;
    }

    /// <summary>
    ///     Order messages with the same timestamp deterministically in Sql Server queues on the classic
    ///     storage layout, and add the <c>queue_order</c> column to classic queue tables that already exist
    ///     without it. This is the default from 7.0 for the tables Wolverine creates (GH-5192); queues using
    ///     <see cref="OptimizeQueueThroughput" /> already order by their <c>seq</c> identity and are not changed.
    ///     With <c>AutoCreate.None</c>, apply the schema migration before starting listeners. Configure this
    ///     before startup; changing it at runtime is not supported.
    /// </summary>
    public SqlServerPersistenceExpression EnableDeterministicQueueOrdering()
    {
        Transport.EnableDeterministicQueueOrdering = true;
        return this;
    }

    /// <summary>
    ///     GH-5192. Turn off the <c>queue_order</c> tie-breaker that 7.0 adds to the classic queue tables it
    ///     creates, and dequeue them in timestamp order only, as Wolverine 6.x did. Tables that already have the
    ///     column keep it. Configure this before startup.
    /// </summary>
    public SqlServerPersistenceExpression DisableDeterministicQueueOrdering()
    {
        Transport.EnableDeterministicQueueOrdering = false;
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