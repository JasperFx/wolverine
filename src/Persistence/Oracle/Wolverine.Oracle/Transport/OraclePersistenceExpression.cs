using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.Oracle.Transport;

public class OraclePersistenceExpression : BrokerExpression<OracleTransport, OracleQueue, OracleQueue, OracleListenerConfiguration, OracleSubscriberConfiguration, OraclePersistenceExpression>
{
    private readonly WolverineOptions _options;

    public OraclePersistenceExpression(OracleTransport transport, WolverineOptions options) : base(transport, options)
    {
        _options = options;
    }

    protected override OracleListenerConfiguration createListenerExpression(OracleQueue listenerEndpoint)
    {
        return new OracleListenerConfiguration(listenerEndpoint);
    }

    protected override OracleSubscriberConfiguration createSubscriberExpression(OracleQueue subscriberEndpoint)
    {
        return new OracleSubscriberConfiguration(subscriberEndpoint);
    }

    /// <summary>
    /// Configure the schema name for the transport queue and scheduled message tables
    /// </summary>
    public OraclePersistenceExpression TransportSchemaName(string schemaName)
    {
        Transport.TransportSchemaName = schemaName.ToUpperInvariant();
        return this;
    }

    /// <summary>
    ///     Order messages with the same timestamp deterministically in Oracle queues, and add the
    ///     <c>queue_order</c> column to queue tables that already exist without it. Deterministic ordering is the
    ///     default from 7.0 for the queue tables Wolverine creates (GH-5192); call this only to migrate existing
    ///     tables too. Adding the identity numbers every row of each table, so drain the queue first. With <c>AutoCreate.None</c>, apply the schema migration before starting
    ///     listeners. Configure this before startup; changing it at runtime is not supported.
    /// </summary>
    public OraclePersistenceExpression EnableDeterministicQueueOrdering()
    {
        Transport.EnableDeterministicQueueOrdering = true;
        return this;
    }

    /// <summary>
    ///     GH-5192. Turn off the <c>queue_order</c> tie-breaker that 7.0 adds to the queue tables it creates, and
    ///     dequeue in timestamp order only, as Wolverine 6.x did. Tables that already have the column keep it.
    ///     Configure this before startup.
    /// </summary>
    public OraclePersistenceExpression DisableDeterministicQueueOrdering()
    {
        Transport.EnableDeterministicQueueOrdering = false;
        return this;
    }

    /// <summary>
    /// Disable inbox and outbox usage on all Oracle Transport endpoints
    /// </summary>
    public OraclePersistenceExpression DisableInboxAndOutboxOnAll()
    {
        var policy = new LambdaEndpointPolicy<OracleQueue>((e, _) =>
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
