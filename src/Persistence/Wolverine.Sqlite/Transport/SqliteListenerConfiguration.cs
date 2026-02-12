using Wolverine.Configuration;

namespace Wolverine.Sqlite.Transport;

public class SqliteListenerConfiguration : ListenerConfiguration<SqliteListenerConfiguration, SqliteQueue>
{
    public SqliteListenerConfiguration(SqliteQueue endpoint) : base(endpoint)
    {
    }

    /// <summary>
    /// The maximum number of messages to receive in a single batch when listening
    /// in either buffered or durable modes. The default is 20.
    /// </summary>
    /// <param name="maximumMessages"></param>
    /// <returns></returns>
    public SqliteListenerConfiguration MaximumMessagesToReceive(int maximumMessages)
    {
        add(e => e.MaximumMessagesToReceive = maximumMessages);
        return this;
    }
}
