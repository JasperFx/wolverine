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