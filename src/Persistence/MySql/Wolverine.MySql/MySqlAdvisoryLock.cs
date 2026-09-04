using System.Data;
using JasperFx.Events.Daemon;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Weasel.Core;

namespace Wolverine.MySql;

/// <summary>
/// MySQL implementation of advisory locks using GET_LOCK/RELEASE_LOCK.
/// MySQL named locks are connection-scoped (automatically released on disconnect).
/// Lock names are limited to 64 characters.
/// </summary>
internal class MySqlAdvisoryLock : IAdvisoryLock
{
    // GH-4261. One long-lived MySqlConnection shared by a SYNCHRONOUS HasLock and three ASYNC methods,
    // with nothing serialising them. Reported against Postgres, where the interleaving desynced the
    // driver's protocol and parked shutdown forever inside an uncancellable CloseAsync; MySqlConnector
    // likewise does not support concurrent operations on one connection, and this class has the
    // identical shape. The two callers are reachable together: writeHeartbeats / executeHealthChecks
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

    private readonly string _databaseName;
    private readonly List<int> _locks = new();
    private readonly ILogger _logger;
    private readonly MySqlDataSource _source;
    private MySqlConnection? _conn;

    public MySqlAdvisoryLock(MySqlDataSource source, ILogger logger, string databaseName)
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

        // MySQL named locks (GET_LOCK / RELEASE_LOCK) are session-scoped,
        // so the lock evaporates the moment the connection's MySQL session
        // dies — KILL CONNECTION, network drop, idle-cull. MySqlConnection
        // doesn't surface that immediately, so we ping. Without this,
        // HasLock keeps returning true after the lock has been transferred
        // and two nodes race as leader. See GH-2602.
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
            // Idempotent against repeated calls on the same session. MySQL named locks stack:
            // GET_LOCK on a name this session already holds succeeds and increments the session's
            // hold count, and the docs are explicit that "if a lock is obtained a second time, it
            // must be released twice." The a84d6a262 heartbeat-renewal change calls
            // TryAttainLeadershipLockAsync every tick — including ticks where the leader already
            // holds the lock — so without this short-circuit the leader's hold count grows by one
            // per heartbeat. The single ReleaseLeadershipLockAsync call during DisableAgentsAsync
            // or stepDownAsync then only decrements once, leaving the lock still held server-side
            // and _locks non-empty, so the connection is never closed either. Failover stalls
            // silently: no error logged, just an election a new leader can never win.
            //
            // GH-4261: hasLockUnsafe, not HasLock — we already hold the gate, and SemaphoreSlim is
            // not reentrant.
            if (hasLockUnsafe(lockId))
            {
                return true;
            }

            if (_conn == null)
            {
                _conn = _source.CreateConnection();
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

            var lockName = ToLockName(lockId);

            await using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT GET_LOCK(@lockName, 0)";
            cmd.Parameters.AddWithValue("@lockName", lockName);

            var result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);

            // GET_LOCK returns 1 if lock was obtained, 0 if timeout (we used 0 for non-blocking), NULL on error
            if (result is int intResult && intResult == 1)
            {
                addId(lockId);
                return true;
            }

            // For MySQL 8.0+, result might be long
            if (result is long longResult && longResult == 1)
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
        if (!holdsId(lockId))
        {
            return;
        }

        // GH-4261: bounded rather than indefinite. If the gate is still held past the budget something
        // is wedged on the connection, and waiting longer turns a released lock into a hung shutdown.
        // MySQL releases named locks when the session ends, so an abandoned lock clears itself when this
        // process exits.
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

            using var cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(1.Seconds());

            var lockName = ToLockName(lockId);

            await using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT RELEASE_LOCK(@lockName)";
                cmd.Parameters.AddWithValue("@lockName", lockName);

                try
                {
                    await cmd.ExecuteScalarAsync(cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Ignore timeout - lock will be released when connection closes
                }
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
        if (_conn == null)
        {
            return;
        }

        if (!await _gate.WaitAsync(CloseBudget).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "Timed out waiting to dispose the advisory lock connection for database {Identifier}; abandoning it",
                _databaseName);
            return;
        }

        try
        {
            if (_conn == null)
            {
                return;
            }

            try
            {
                foreach (var lockId in heldIds())
                {
                    var lockName = ToLockName(lockId);

                    await using var cmd = _conn.CreateCommand();
                    cmd.CommandText = "SELECT RELEASE_LOCK(@lockName)";
                    cmd.Parameters.AddWithValue("@lockName", lockName);

                    await cmd.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to dispose of advisory locks for database {Identifier}",
                    _databaseName);
            }

            await safeCloseConnectionAsync().ConfigureAwait(false);
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
    /// GH-4261. <see cref="MySqlConnection.CloseAsync"/> takes no <see cref="CancellationToken"/>, so a
    /// caller's shutdown budget cannot reach it. The gate above should stop a connection ever getting
    /// into a state where the close does not return; this makes it survivable if one ever does. Returns
    /// false when the close was abandoned.
    /// </summary>
    private async Task<bool> closeWithinBudgetAsync(MySqlConnection conn)
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

    /// <summary>
    /// Convert integer lock ID to a MySQL lock name string.
    /// MySQL lock names are limited to 64 characters.
    /// </summary>
    private static string ToLockName(int lockId)
    {
        return $"wolverine_{lockId}";
    }
}
