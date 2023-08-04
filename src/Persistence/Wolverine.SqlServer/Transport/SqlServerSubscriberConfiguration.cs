using Wolverine.Configuration;

namespace Wolverine.SqlServer.Transport;

public class SqlServerSubscriberConfiguration : SubscriberConfiguration<SqlServerSubscriberConfiguration, SqlServerQueue>
{
    public SqlServerSubscriberConfiguration(SqlServerQueue endpoint) : base(endpoint)
    {
    }
}