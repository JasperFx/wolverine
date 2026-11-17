using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.MySql.Transport;

public class MySqlPersistenceExpression : BrokerExpression<MySqlTransport, MySqlQueue, MySqlQueue, MySqlListenerConfiguration, MySqlSubscriberConfiguration, MySqlPersistenceExpression>
{
    private readonly WolverineOptions _options;

    public MySqlPersistenceExpression(MySqlTransport transport, WolverineOptions options) : base(transport, options)
    {
        _options = options;
    }

    protected override MySqlListenerConfiguration createListenerExpression(MySqlQueue listenerEndpoint)
    {
        return new MySqlListenerConfiguration(listenerEndpoint);
    }

    protected override MySqlSubscriberConfiguration createSubscriberExpression(MySqlQueue subscriberEndpoint)
    {
        return new MySqlSubscriberConfiguration(subscriberEndpoint);
    }

    /// <summary>
    /// Configure the schema name for the transport queue and scheduled message tables
    /// </summary>
    public MySqlPersistenceExpression TransportSchemaName(string schemaName)
    {
        schemaName.AssertValidSchemaName();
        Transport.TransportSchemaName = schemaName;
        return this;
    }

    /// <summary>
    ///     Order messages with the same timestamp deterministically in MySQL queues, and add the
    ///     <c>queue_order</c> column to queue tables that already exist without it. Deterministic ordering is the
    ///     default from 7.0 for the queue tables Wolverine creates (GH-5192); call this only to migrate existing
    ///     tables too. Adding the column rewrites each table and numbers its rows, so drain the queue first. With <c>AutoCreate.None</c>, apply the schema migration before starting
    ///     listeners. Configure this before startup; changing it at runtime is not supported.
    /// </summary>
    public MySqlPersistenceExpression EnableDeterministicQueueOrdering()
    {
        Transport.EnableDeterministicQueueOrdering = true;
        return this;
    }

    /// <summary>
    ///     GH-5192. Turn off the <c>queue_order</c> tie-breaker that 7.0 adds to the queue tables it creates, and
    ///     dequeue in timestamp order only, as Wolverine 6.x did. Tables that already have the column keep it.
    ///     Configure this before startup.
    /// </summary>
    public MySqlPersistenceExpression DisableDeterministicQueueOrdering()
    {
        Transport.EnableDeterministicQueueOrdering = false;
        return this;
    }

    /// <summary>
    /// Disable inbox and outbox usage on all MySQL Transport endpoints
    /// </summary>
    /// <returns></returns>
    public MySqlPersistenceExpression DisableInboxAndOutboxOnAll()
    {
        var policy = new LambdaEndpointPolicy<MySqlQueue>((e, _) =>
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
