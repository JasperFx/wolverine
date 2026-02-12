using Wolverine.Configuration;

namespace Wolverine.Sqlite.Transport;

public class SqliteSubscriberConfiguration : SubscriberConfiguration<SqliteSubscriberConfiguration, SqliteQueue>
{
    public SqliteSubscriberConfiguration(SqliteQueue endpoint) : base(endpoint)
    {
    }
}
