using Wolverine.Configuration;

namespace Wolverine.Postgresql.Transport;

public class PostgresqlSubscriberConfiguration : SubscriberConfiguration<PostgresqlSubscriberConfiguration, PostgresqlQueue>
{
    public PostgresqlSubscriberConfiguration(PostgresqlQueue endpoint) : base(endpoint)
    {
    }
}