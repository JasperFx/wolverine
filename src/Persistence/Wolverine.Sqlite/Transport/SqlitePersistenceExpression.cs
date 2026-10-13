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
    ///     Opt into deterministic ordering for messages with the same timestamp in SQLite
    ///     queues. This adds an index to each of the ready and scheduled queue tables. With
    ///     <c>AutoCreate.None</c>, apply the schema migration before starting listeners with this
    ///     option enabled. Configure this before startup; changing it at runtime is not supported.
    /// </summary>
    public SqlitePersistenceExpression EnableDeterministicQueueOrdering()
    {
        _transport.EnableDeterministicQueueOrdering = true;
        return this;
    }

    /// <summary>
    ///     GH-3947: prefix the names of the SQLite queue and scheduled-message tables. SQLite has no
    ///     schemas, so <c>PrefixName("reporting")</c> names the tables for an <c>orders</c> queue
    ///     <c>reporting_wolverine_queue_orders</c> and <c>reporting_wolverine_queue_orders_scheduled</c>.
    ///     Without this, or with <c>"main"</c>, the tables keep their historical, unprefixed names.
    ///     Every application sending to or listening on a queue must use the same prefix.
    /// </summary>
    public SqlitePersistenceExpression PrefixName(string prefixName)
    {
        _transport.PrefixName = prefixName;
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
