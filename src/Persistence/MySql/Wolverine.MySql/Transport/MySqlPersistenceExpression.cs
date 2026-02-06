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
