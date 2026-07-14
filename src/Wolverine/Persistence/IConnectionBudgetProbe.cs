namespace Wolverine.Persistence;

/// <summary>
/// Optional capability for a message store whose backing engine can report how many connections
/// are currently open against its *server*. Implemented alongside
/// <see cref="Durability.IMessageStore"/> — the metrics sweeper looks for it and skips stores that
/// don't have it, so a provider without a cheap server-wide connection count simply doesn't
/// participate. See wolverine#3397.
/// </summary>
public interface IConnectionBudgetProbe
{
    /// <summary>
    /// The server this store lives on. Many stores share one <see cref="DatabaseServerId"/> in a
    /// sharded deployment; the sweeper dedupes on it so the probe runs once per server per pass
    /// no matter how many tenant databases this node happens to own.
    /// </summary>
    DatabaseServerId ServerId { get; }

    /// <summary>
    /// Count the connections currently open on the server. Must be one cheap query — it runs every
    /// metrics sweep, and a probe that is itself expensive under connection pressure would be
    /// making the problem it measures worse.
    /// </summary>
    ValueTask<int> CountServerConnectionsAsync(CancellationToken token);

    /// <summary>
    /// Read the server's own connection limit, for use only when the application hasn't declared a
    /// budget for this server. Return null when the value can't be read (e.g. SQL Server without
    /// <c>VIEW SERVER STATE</c>), which surfaces as <see cref="ConnectionBudgetSource.Unknown"/>
    /// rather than an error. Callers invoke this at most once per server per process — the limit
    /// doesn't move at runtime.
    /// </summary>
    ValueTask<int?> ProbeMaxConnectionsAsync(CancellationToken token);
}
