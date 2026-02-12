namespace Wolverine.Sqlite;

/// <summary>
/// Wolverine-specific subclass that defers keep-alive cleanup.
/// WolverineRuntime.StopAsync may still need database access after the service provider
/// disposes the message store (and its DataSource). Closing the keep-alive in Dispose
/// would destroy the in-memory database too early. Instead, the keep-alive is left open
/// and cleaned up by the GC finalizer. The base class's disposed flag causes subsequent
/// operations to throw ObjectDisposedException, which the runtime's exception handler catches.
/// </summary>
internal class WolverineSqliteDataSource : Weasel.Sqlite.SqliteDataSource
{
    public WolverineSqliteDataSource(string connectionString) : base(connectionString)
    {
    }

    protected override void Dispose(bool disposing)
    {
        // Intentionally do NOT call CloseKeepAlive() here.
        // Let the base set its disposed flag but keep the in-memory DB alive
        // until the runtime finishes cleanup.
    }
}
