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
