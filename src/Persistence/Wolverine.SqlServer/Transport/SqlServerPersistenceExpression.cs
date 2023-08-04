using Wolverine.Transports;

namespace Wolverine.SqlServer.Transport;

public class SqlServerPersistenceExpression : BrokerExpression<SqlServerTransport, SqlServerQueue, SqlServerQueue, SqlServerListenerConfiguration, SqlServerSubscriberConfiguration, SqlServerPersistenceExpression>
{
    public SqlServerPersistenceExpression(SqlServerTransport transport, WolverineOptions options) : base(transport, options)
    {
    }

    protected override SqlServerListenerConfiguration createListenerExpression(SqlServerQueue listenerEndpoint)
    {
        return new SqlServerListenerConfiguration(listenerEndpoint);
    }

    protected override SqlServerSubscriberConfiguration createSubscriberExpression(SqlServerQueue subscriberEndpoint)
    {
        return new SqlServerSubscriberConfiguration(subscriberEndpoint);
    }
}