using Wolverine.Configuration;

namespace Wolverine.Sqlite.Transport;

public class SqlitePersistenceExpression
{
    private readonly SqliteTransport _transport;
    private readonly WolverineOptions _options;

    internal SqlitePersistenceExpression(SqliteTransport transport, WolverineOptions options)
    {
        _transport = transport;
        _options = options;
    }

    /// <summary>
    /// Automatically purge all existing SQLite queues on application startup
    /// </summary>
    /// <returns></returns>
    public SqlitePersistenceExpression AutoPurgeOnStartup()
    {
        _transport.AutoPurgeAllQueues = true;
        return this;
    }

    /// <summary>
    /// Automatically provision any missing SQLite queue or scheduled message
    /// tables on application startup
    /// </summary>
    /// <returns></returns>
    public SqlitePersistenceExpression AutoProvision()
    {
        _transport.AutoProvision = true;
        return this;
    }

    /// <summary>
    /// Disable all inbox or outbox behavior across all SQLite messaging endpoints
    /// </summary>
    /// <returns></returns>
    public SqlitePersistenceExpression DisableInboxAndOutboxOnAll()
    {
        _options.Policies.DisableConventionalLocalRouting();
        return this;
    }

}
