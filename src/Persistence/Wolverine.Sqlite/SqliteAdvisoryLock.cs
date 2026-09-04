using System.Data;
using JasperFx.Events.Daemon;
using System.Data.Common;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace Wolverine.Sqlite;

internal class SqliteAdvisoryLock : IAdvisoryLock
{
    // wolverine_locks rows are not bound to the writing connection (unlike the
    // BEGIN EXCLUSIVE migration lock), so a hard-killed holder leaves a row
    // that no peer would ever reap. Pair a TTL sweep on each attempt with a
    // heartbeat refresh of acquired_at on each re-attempt by the live holder:
    // - Live holders re-attain on every poll tick (HealthCheckPollingTime,
    //   ScheduledJobPollingTime), which advances acquired_at well inside TTL.
    // - A dead holder stops refreshing; peers reap the row once it ages past
    //   TTL on a subsequent attempt.
    // TTL must be > 2× the slowest poll cadence using this lock. Default 2m
    // accommodates the 10s heartbeat default with healthy headroom for GC
    // pauses, slow recovery cycles, or temporary I/O stalls.
    internal static readonly TimeSpan DefaultLockTtl = TimeSpan.FromMinutes(2);

    // GH-4261. One long-lived DbConnection shared by a SYNCHRONOUS HasLock and three ASYNC methods,
    // with nothing serialising them. Reported against Postgres, where the interleaving desynced the
    // driver's protocol and parked shutdown forever inside an uncancellable CloseAsync; this class has
    // the identical shape, and SQLite additionally takes one writer at a time, so two callers on one
    // connection is not something to leave to chance. The two callers are reachable together:
    // writeHeartbeats / executeHealthChecks (WolverineRuntime.Agents.cs) run on the runtime-wide
    // Cancellation token, which shutdownAsync only cancels AFTER teardownAgentsAsync returns, and
    // teardownAgentsAsync "stops" them with Task.SafeDispose() -- which disposes the Task object and
    // does nothing to the running loop.
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

    private readonly DbDataSource _dataSource;
    private readonly ILogger _logger;
    private readonly string _databaseName;
    private readonly TimeSpan _lockTtl;
    private readonly List<int> _locks = new();
    private DbConnection? _conn;

    public SqliteAdvisoryLock(DbDataSource dataSource, ILogger logger, string databaseName)
        : this(dataSource, logger, databaseName, DefaultLockTtl)
    {
    }

    internal SqliteAdvisoryLock(DbDataSource dataSource, ILogger logger, string databaseName, TimeSpan lockTtl)
    {
        _dataSource = dataSource;
        _logger = logger;
        _databaseName = databaseName;
        _lockTtl = lockTtl;
    }

    public bool HasLock(int lockId)
    {
        // Cheap negative outside the gate: an id we never took cannot be held.
        if (!holdsId(lockId)) return false;

        if (!_gate.Wait(GateTimeout))
        {
            // GH-4261: a busy gate means another advisory-lock operation on THIS node is in flight, not
            // that the row was reaped. A wrong `false` here is read by
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

        // SQLite advisory locks are table rows; in single-process tests
        // the connection is unlikely to die out from under us, but for
        // parity with the Postgres / MySQL fix and to detect any held
        // connection that has gone bad (e.g. file deleted under us),
        // ping before reporting the lock as held. See GH-2602.
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
            // Idempotent: if we already hold this lock and the connection is healthy,
            // re-attempting must report success. The previous implementation would run
            // INSERT OR IGNORE again, get result==0, and falsely return false.
            //
            // GH-4261: hasLockUnsafe, not HasLock — we already hold the gate, and SemaphoreSlim is not
            // reentrant.
            if (hasLockUnsafe(lockId))
            {
                await refreshHeartbeatAsync(lockId, token).ConfigureAwait(false);
                return true;
            }

            if (_conn == null)
            {
                _conn = await _dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
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

            try
            {
                // SQLite doesn't have advisory locks like PostgreSQL.
                // We use a row in wolverine_locks; the table is created by the message
                // store's normal schema migration. The migration lock itself uses
                // BEGIN EXCLUSIVE (see SqliteMessageStore.acquireMigrationLockAsync) so
                // there is no chicken-and-egg between this table and migration.
                //
                // Stale-row sweep: if a previous holder died without releasing, its
                // row would block all peers forever. Reap rows whose acquired_at is
                // older than TTL before attempting INSERT OR IGNORE. Live holders
                // refresh acquired_at on every re-attempt, so they're never reaped.
                await _conn.CreateCommand(
                        "DELETE FROM wolverine_locks WHERE lock_id = @lockId AND acquired_at < @cutoff")
                    .With("lockId", lockId)
                    .With("cutoff", DateTime.UtcNow.Subtract(_lockTtl).ToString("yyyy-MM-dd HH:mm:ss"))
                    .ExecuteNonQueryAsync(token);

                var result = await _conn.CreateCommand("INSERT OR IGNORE INTO wolverine_locks (lock_id, acquired_at) VALUES (@lockId, datetime('now'))")
                    .With("lockId", lockId)
                    .ExecuteNonQueryAsync(token);

                if (result > 0)
                {
                    addId(lockId);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error trying to attain advisory lock {LockId}", lockId);
                return false;
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
    private async Task refreshHeartbeatAsync(int lockId, CancellationToken token)
    {
        if (_conn == null) return;

        try
        {
            await _conn.CreateCommand(
                    "UPDATE wolverine_locks SET acquired_at = datetime('now') WHERE lock_id = @lockId")
                .With("lockId", lockId)
                .ExecuteNonQueryAsync(token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to refresh advisory-lock heartbeat for {LockId} on database {Database}; lock may be reaped if the failure persists past TTL",
                lockId, _databaseName);
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
        // The row's TTL sweep reaps an abandoned lock, so this is recoverable without us.
        if (!await _gate.WaitAsync(CloseBudget).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "Timed out waiting to release advisory lock {LockId} for database {Identifier}; leaving it to the TTL sweep",
                lockId, _databaseName);
            return;
        }

        try
        {
            await releaseLockUnsafeAsync(lockId).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Callers MUST hold <c>_gate</c>. Split out because <see cref="DisposeAsync"/> releases every held
    /// lock in a loop and SemaphoreSlim is not reentrant -- going back through the public method would
    /// deadlock against the gate this method's caller already holds.
    /// </summary>
    private async Task releaseLockUnsafeAsync(int lockId)
    {
        if (_conn == null || _conn.State == ConnectionState.Closed)
        {
            removeId(lockId);
            return;
        }

        try
        {
            await _conn.CreateCommand("DELETE FROM wolverine_locks WHERE lock_id = @lockId")
                .With("lockId", lockId)
                .ExecuteNonQueryAsync();
            removeId(lockId);

            if (!anyIdsHeld())
            {
                await safeCloseConnectionAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error trying to release advisory lock {LockId}", lockId);
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
            foreach (var lockId in heldIds())
            {
                await releaseLockUnsafeAsync(lockId).ConfigureAwait(false);
            }

            // releaseLockUnsafeAsync nulls _conn once the last lock is released; safeCloseConnectionAsync
            // no-ops on a null connection, so it covers the released-some path without a second null check.
            await safeCloseConnectionAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to dispose of advisory locks for database {Identifier}",
                _databaseName);
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
    /// GH-4261. <see cref="DbConnection.CloseAsync"/> takes no <see cref="CancellationToken"/>, so a
    /// caller's shutdown budget cannot reach it. The gate above should stop a connection ever getting
    /// into a state where the close does not return; this makes it survivable if one ever does. Returns
    /// false when the close was abandoned.
    /// </summary>
    private async Task<bool> closeWithinBudgetAsync(DbConnection conn)
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
