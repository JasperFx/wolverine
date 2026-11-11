using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.Postgresql.Transport;

public class PostgresqlPersistenceExpression : BrokerExpression<PostgresqlTransport, PostgresqlQueue, PostgresqlQueue, PostgresqlListenerConfiguration, PostgresqlSubscriberConfiguration, PostgresqlPersistenceExpression>
{
    private readonly WolverineOptions _options;

    public PostgresqlPersistenceExpression(PostgresqlTransport transport, WolverineOptions options) : base(transport, options)
    {
        _options = options;
    }

    protected override PostgresqlListenerConfiguration createListenerExpression(PostgresqlQueue listenerEndpoint)
    {
        return new PostgresqlListenerConfiguration(listenerEndpoint);
    }

    protected override PostgresqlSubscriberConfiguration createSubscriberExpression(PostgresqlQueue subscriberEndpoint)
    {
        return new PostgresqlSubscriberConfiguration(subscriberEndpoint);
    }

    public PostgresqlPersistenceExpression TransportSchemaName(string schemaName)
    {
        schemaName.AssertValidSchemaName();
        Transport.TransportSchemaName = schemaName;
        return this;
    }

    /// <summary>
    ///     Order messages with the same timestamp deterministically in PostgreSQL queues, and add the
    ///     <c>queue_order</c> column to queue tables that already exist without it. Deterministic ordering
    ///     is the default from 7.0 for the queue tables Wolverine creates; call this only to migrate
    ///     existing tables too. Adding the identity rewrites each table under an exclusive lock, so
    ///     drain the queue first. With <c>AutoCreate.None</c>, apply the schema migration before starting
    ///     listeners. Configure this before startup; changing it at runtime is not supported.
    /// </summary>
    public PostgresqlPersistenceExpression EnableDeterministicQueueOrdering()
    {
        Transport.EnableDeterministicQueueOrdering = true;
        return this;
    }

    /// <summary>
    ///     GH-5192. Turn off the <c>queue_order</c> tie-breaker that 7.0 adds to the queue tables it creates,
    ///     and dequeue in timestamp order only, as Wolverine 6.x did. Tables that already have the column
    ///     keep it. Configure this before startup.
    /// </summary>
    public PostgresqlPersistenceExpression DisableDeterministicQueueOrdering()
    {
        Transport.EnableDeterministicQueueOrdering = false;
        return this;
    }

    /// <summary>
    ///     Opt into PostgreSQL LISTEN/NOTIFY wakeups for native queues. This holds one dedicated
    ///     connection per active Npgsql data source (typically one per queue database) and application
    ///     process; polling remains enabled as a fallback. Configure this before startup. With PgBouncer,
    ///     LISTEN requires session pooling. Multiplexed Npgsql data sources are not supported.
    /// </summary>
    public PostgresqlPersistenceExpression EnableQueueNotifications()
    {
        Transport.EnableQueueNotifications = true;
        return this;
    }

    /// <summary>
    /// Disable inbox and outbox usage on all Sql Server Transport endpoints
    /// </summary>
    /// <returns></returns>
    public PostgresqlPersistenceExpression DisableInboxAndOutboxOnAll()
    {
        var policy = new LambdaEndpointPolicy<PostgresqlQueue>((e, _) =>
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
