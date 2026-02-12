using Wolverine.Configuration;

namespace Wolverine.Oracle.Transport;

public class OracleSubscriberConfiguration : SubscriberConfiguration<OracleSubscriberConfiguration, OracleQueue>
{
    public OracleSubscriberConfiguration(OracleQueue endpoint) : base(endpoint)
    {
    }
}
