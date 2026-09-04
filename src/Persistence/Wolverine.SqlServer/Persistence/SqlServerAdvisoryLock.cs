using System.Data;
using JasperFx.Events.Daemon;
using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Weasel.SqlServer;

namespace Wolverine.SqlServer.Persistence;

/// <summary>
/// Wolverine-owned <see cref="IAdvisoryLock"/> for SQL Server. Equivalent to
/// <c>Weasel.SqlServer.AdvisoryLock</c> but with a server-side liveness ping
/// in <see cref="HasLock"/> so a stale leader whose Postgres / SQL Server
/// session has been killed (KILL SPID, AlwaysOn failover, idle-connection
/// drop, NAT gateway reuse, etc.) detects the lock loss instead of forever
/// claiming to be the leader.
///
/// See https://github.com/JasperFx/wolverine/issues/2602.
/// </summary>
internal class SqlServerAdvisoryLock : IAdvisoryLock
{
    // GH-4261. One long-lived SqlConnection shared by a SYNCHRONOUS HasLock and three ASYNC methods,
    // with nothing serialising them. Reported against Postgres, where the interleaving desynced the
    // Npgsql protocol and parked shutdown forever inside an uncancellable CloseAsync; SqlConnection is
    // likewise documented as not supporting concurrent operations, and this class has the identical
    // shape. The two callers are reachable together: writeHeartbeats / executeHealthChecks
    // (WolverineRuntime.Agents.cs) run on the runtime-wide Cancellation token, which shutdownAsync only
    // cancels AFTER teardownAgentsAsync returns, and teardownAgentsAsync "stops" them with
    // Task.SafeDispose() -- which disposes the Task object and does nothing to the running loop.
    //
    // _gate serialises every touch of _conn. _locksLock guards the held-id list separately, so the one
    // path that deliberately does not wait for the gate -- HasLock on timeout -- can still read it
    // safely. Ordering is always _gate then _locksLock, never the reverse.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _locksLock = new();

    // Short on purpose: HasLock is synchronous and sits on the health-check tick.
    private static readonly TimeSpan GateTimeout = 250.Milliseconds();

    // Generous on purpose: this bounds shutdown paths, where the alternative is an unbounded hang.
    private static readonly TimeSpan CloseBudget = 5.Seconds();

    private readonly Func<SqlConnection> _source;
    private readonly ILogger _logger;
    private readonly string _databaseName;
    private readonly List<int> _locks = new();
    private SqlConnection? _conn;

    public SqlServerAdvisoryLock(Func<SqlConnection> source, ILogger logger, string databaseName)
    {
        _source = source;
        _logger = logger;
        _databaseName = databaseName;
    }

    public bool HasLock(int lockId)
    {
        // Cheap negative outside the gate: an id we never took cannot be held.
        if (!holdsId(lockId)) return false;

        if (!_gate.Wait(GateTimeout))
        {
            // GH-4261: a busy gate means another advisory-lock operation on THIS node is in flight, not
            // that the server dropped our session. A wrong `false` here is read by
            // NodeAgentController.DoHealthChecksInternalAsync as lost leadership and fires
            // stepDownAsync -- exactly the churn GH-2602 and GH-3604 were fighting. Report the last
            // state we established and skip the keepalive for this tick.
            _logger.LogDebug(
                "Advisory lock connection for database {Database} was busy; reporting the last known state of lock {LockId} without pinging",
                _databaseName, lockId);
            return true;
        }

        try
        {
            return hasLockUnsafe(lockId);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The GH-2602 liveness ping. Callers MUST hold <c>_gate</c> -- it does synchronous I/O on the
    /// shared connection and may replace it.
    /// </summary>
    private bool hasLockUnsafe(int lockId)
    {
        if (_conn is null) return false;
        if (!holdsId(lockId)) return false;

        // SQL Server session-scoped application locks (sp_getapplock /
        // sp_releaseapplock) are released the instant the SQL session ends —
        // KILL SPID, network drop, AlwaysOn failover, AAD token expiry on
        // managed identity. SqlConnection.State stays Open until we use it,
        // so without this ping HasLock keeps reporting the lock held long
        // after another session has acquired it. See GH-2602.
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "select 1";
            cmd.CommandTimeout = 2;
            cmd.ExecuteScalar();
            return true;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e,
                "Lost advisory-lock connection for database {Database}; clearing held lock ids {Locks}",
                _databaseName, heldIds());

            clearIds();
            try
            {
                _conn.Dispose();
            }
            catch
            {
                // Already broken; nothing to do.
            }
            _conn = null;
            return false;
        }
    }

    private bool holdsId(int lockId)
    {
        lock (_locksLock) return _locks.Contains(lockId);
    }

    private bool anyIdsHeld()
    {
        lock (_locksLock) return _locks.Count > 0;
    }

    private int[] heldIds()
    {
        lock (_locksLock) return _locks.ToArray();
    }

    private void addId(int lockId)
    {
        lock (_locksLock) _locks.Add(lockId);
    }

    private void removeId(int lockId)
    {
        lock (_locksLock) _locks.Remove(lockId);
    }

    private void clearIds()
    {
        lock (_locksLock) _locks.Clear();
    }

    public async Task<bool> TryAttainLockAsync(int lockId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);

        try
        {
            // Idempotent against repeated calls on the same session. SQL Server
            // session-scoped application locks (sp_getapplock) are reentrant —
            // "If a lock has been requested in the current transaction or by the
            // current session, sp_getapplock can be called multiple times for it
            // (with the same name and lock owner). For each request that returns
            // success ... sp_releaseapplock must also be called." The
            // a84d6a262 heartbeat-renewal change calls TryAttainLeadershipLockAsync
            // every tick — including ticks where the leader already holds the
            // lock — so without this short-circuit the leader's lock count grows
            // by one per heartbeat. The single ReleaseLeadershipLockAsync call
            // during DisableAgentsAsync or stepDownAsync then only decrements
            // once, leaving the lock still held server-side and silently
            // blocking failover (no error logged, just a stalled election).
            //
            // GH-4261: hasLockUnsafe, not HasLock — we already hold the gate, and SemaphoreSlim is not
            // reentrant.
            if (hasLockUnsafe(lockId))
            {
                return true;
            }

            if (_conn == null)
            {
                _conn = _source();
                await _conn.OpenAsync(token).ConfigureAwait(false);
            }

            if (_conn.State == ConnectionState.Closed)
            {
                try
                {
                    await _conn.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error trying to clean up and restart an advisory lock connection");
                }
                finally
                {
                    _conn = null;
                }

                return false;
            }

            var attained = await _conn.TryGetGlobalLock(lockId.ToString(), cancellation: token).ConfigureAwait(false);
            if (attained)
            {
                addId(lockId);
                return true;
            }

            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReleaseLockAsync(int lockId)
    {
        if (!holdsId(lockId)) return;

        // GH-4261: bounded rather than indefinite. If the gate is still held past the budget something
        // is wedged on the connection, and waiting longer turns a released lock into a hung shutdown.
        // SQL Server drops session-scoped application locks when the session ends, so an abandoned lock
        // clears itself when this process exits.
        if (!await _gate.WaitAsync(CloseBudget).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "Timed out waiting to release advisory lock {LockId} for database {Identifier}; leaving it to be released when the session ends",
                lockId, _databaseName);
            return;
        }

        try
        {
            if (_conn == null || _conn.State == ConnectionState.Closed)
            {
                removeId(lockId);
                return;
            }

            try
            {
                using var cancellation = new CancellationTokenSource();
                cancellation.CancelAfter(1.Seconds());

                await _conn.ReleaseGlobalLock(lockId.ToString(), cancellation: cancellation.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogDebug(e,
                    "Error trying to release advisory lock {LockId} for database {Identifier}",
                    lockId, _databaseName);
            }

            removeId(lockId);

            if (!anyIdsHeld())
            {
                await safeCloseConnectionAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_conn == null) return;

        if (!await _gate.WaitAsync(CloseBudget).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "Timed out waiting to dispose the advisory lock connection for database {Identifier}; abandoning it",
                _databaseName);
            return;
        }

        try
        {
            if (_conn == null) return;

            try
            {
                if (_conn.State == ConnectionState.Open)
                {
                    foreach (var i in heldIds())
                    {
                        try
                        {
                            await _conn.ReleaseGlobalLock(i.ToString(), CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            _logger.LogDebug(e,
                                "Error trying to release advisory lock {LockId} during dispose for database {Identifier}",
                                i, _databaseName);
                        }
                    }
                }

                await safeCloseConnectionAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "Error trying to dispose of advisory locks for database {Identifier}",
                    _databaseName);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Callers MUST hold <c>_gate</c>.
    /// </summary>
    private async Task safeCloseConnectionAsync()
    {
        // GH-4261: take the connection off the field FIRST, so an abandoned close cannot leave a dead
        // connection reachable for the next caller.
        var conn = _conn;
        _conn = null;
        if (conn == null) return;

        try
        {
            if (conn.State == ConnectionState.Open && !await closeWithinBudgetAsync(conn).ConfigureAwait(false))
            {
                // Deliberately no DisposeAsync: that would race the CloseAsync still parked on this
                // connection. Dropping the reference is enough.
                return;
            }

            await conn.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Error trying to close advisory lock connection for database {Identifier}",
                _databaseName);
        }
    }

    /// <summary>
    /// GH-4261. <see cref="SqlConnection.CloseAsync"/> takes no <see cref="CancellationToken"/>, so a
    /// caller's shutdown budget cannot reach it. The gate above should stop a connection ever getting
    /// into a state where the close does not return; this makes it survivable if one ever does. Returns
    /// false when the close was abandoned.
    /// </summary>
    private async Task<bool> closeWithinBudgetAsync(SqlConnection conn)
    {
        var closing = conn.CloseAsync();

        using var delay = new CancellationTokenSource();
        var finished = await Task.WhenAny(closing, Task.Delay(CloseBudget, delay.Token)).ConfigureAwait(false);
        await delay.CancelAsync().ConfigureAwait(false);

        if (!ReferenceEquals(finished, closing))
        {
            _logger.LogWarning(
                "Timed out after {Budget} closing the advisory lock connection for database {Identifier}; abandoning it",
                CloseBudget, _databaseName);

            // Observe the abandoned close, so that if it eventually faults the exception cannot resurface
            // as an UnobservedTaskException on the finalizer thread long after anyone could act on it.
            _ = closing.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return false;
        }

        // Surface a genuine close failure to the caller's catch rather than swallowing it here.
        await closing.ConfigureAwait(false);
        return true;
    }
}
