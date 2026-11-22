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

    /// <summary>
    ///     Configure how often to poll for new messages when the queue is idle.
    ///     If not set, falls back to DurabilitySettings.ScheduledJobPollingTime (default 5s).
    /// </summary>
    public SqliteListenerConfiguration PollingInterval(TimeSpan interval)
    {
        add(e => e.PollingInterval = interval);
        return this;
    }

    /// <summary>
    ///     Configure how often the scheduled message loop moves due scheduled messages into this queue
    ///     and deletes expired rows, separately from <see cref="PollingInterval"/>. If not set, falls back
    ///     to the polling interval, and then to DurabilitySettings.ScheduledJobPollingTime (default 5s).
    /// </summary>
    public SqliteListenerConfiguration ScheduledMessagePollingInterval(TimeSpan interval)
    {
        add(e => e.ScheduledMessagePollingInterval = interval);
        return this;
    }
}
