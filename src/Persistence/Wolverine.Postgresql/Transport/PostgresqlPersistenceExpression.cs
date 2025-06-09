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