using Wolverine.Configuration;

namespace Wolverine.SqlServer.Transport;

public class SqlServerSubscriberConfiguration : SubscriberConfiguration<SqlServerSubscriberConfiguration, SqlServerQueue>
{
    public SqlServerSubscriberConfiguration(SqlServerQueue endpoint) : base(endpoint)
    {
    }

    /// <summary>
    ///     Mark this queue as owned by another system. Wolverine will not create, migrate or drop its queue and
    ///     scheduled message tables at startup, in <c>resources setup</c> or <c>resources teardown</c>, or as part
    ///     of the message store's schema, even when <c>AutoProvision()</c> is on. <c>resources check</c> still
    ///     checks it. GH-5342.
    /// </summary>
    public SqlServerSubscriberConfiguration ExternallyOwned()
    {
        // Set at once as well as when the endpoint compiles: the queue tables are registered with the
        // message store's schema early in startup, before any endpoint is compiled
        if (_endpoint != null) _endpoint.IsExternallyOwned = true;

        add(e => e.IsExternallyOwned = true);
        return this;
    }
}