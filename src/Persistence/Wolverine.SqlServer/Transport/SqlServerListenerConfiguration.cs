using Wolverine.Configuration;

namespace Wolverine.SqlServer.Transport;

public class SqlServerListenerConfiguration : ListenerConfiguration<SqlServerListenerConfiguration, SqlServerQueue>
{
    public SqlServerListenerConfiguration(SqlServerQueue endpoint) : base(endpoint)
    {
    }

    public SqlServerListenerConfiguration(Func<SqlServerQueue> source) : base(source)
    {
    }
}