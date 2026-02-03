using Wolverine.Configuration;

namespace Wolverine.MySql.Transport;

public class MySqlSubscriberConfiguration : SubscriberConfiguration<MySqlSubscriberConfiguration, MySqlQueue>
{
    public MySqlSubscriberConfiguration(MySqlQueue endpoint) : base(endpoint)
    {
    }
}
