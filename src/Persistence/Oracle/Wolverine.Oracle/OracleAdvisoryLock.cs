using System.Data;
using JasperFx.Events.Daemon;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;
using Weasel.Core;
using Wolverine.Oracle.Schema;

namespace Wolverine.Oracle;

/// <summary>
/// Oracle implementation of advisory locks using row-level locks (FOR UPDATE NOWAIT).
/// Uses the wolverine_locks table to hold lock rows.
/// </summary>
internal class OracleAdvisoryLock : IAdvisoryLock
{
    // GH-4261. Unlike its four siblings this one keeps a connection PER LOCK rather than one shared
    // connection, but the hazard is the same: a SYNCHRONOUS HasLock pings held.conn while
    // ReleaseLockAsync / DisposeAsync concurrently roll back that same transaction, close that same
    // connection, and drop it from _heldLocks -- with nothing serialising them, and with _locks and
    // _heldLocks themselves being a plain List and Dictionary mutated from both. Reported against
    // Postgres, where the interleaving desynced the driver's protocol and parked shutdown forever
    // inside an uncancellable CloseAsync. The two callers are reachable together: writeHeartbeats /
    // executeHealthChecks (WolverineRuntime.Agents.cs) run on the runtime-wide Cancellation token,
    // which shutdownAsync only cancels AFTER teardownAgentsAsync returns, and teardownAgentsAsync
    // "stops" them with Task.SafeDispose() -- which disposes the Task object and does nothing to the
    // running loop.
    //
    // _gate serialises every touch of the held connections. _locksLock guards the bookkeeping on its
    // own, so the one path that deliberately does not wait for the gate -- HasLock on timeout -- can
    // still read it safely. Ordering is always _gate then _locksLock, never the reverse.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _locksLock = new();

    // Short on purpose: HasLock is synchronous and sits on the health-check tick.
    private static readonly TimeSpan GateTimeout = 250.Milliseconds();

    // Generous on purpose: this bounds shutdown paths, where the alternative is an unbounded hang.
    private static readonly TimeSpan CloseBudget = 5.Seconds();

    private readonly string _schemaName;
    private readonly List<int> _locks = new();
    private readonly ILogger _logger;
    private readonly OracleDataSource _source;
    private readonly Dictionary<int, (OracleConnection conn, OracleTransaction tx)> _heldLocks = new();

    public OracleAdvisoryLock(OracleDataSource source, ILogger logger, string schemaName)
    {
        _source = source;
        _logger = logger;
        _schemaName = schemaName;
    }

    public bool HasLock(int lockId)
    {
        // Cheap negative outside the gate: an id we never took cannot be held.
        if (!holdsId(lockId)) return false;

        if (!_gate.Wait(GateTimeout))
        {
            // GH-4261: a busy gate means another advisory-lock operation on THIS node is in flight, not
            // that the row lock evaporated. A wrong `false` here is read by
            // NodeAgentController.DoHealthChecksInternalAsync as lost leadership and fires
            // stepDownAsync -- exactly the churn GH-2602 and GH-3604 were fighting. Report the last
            // state we established and skip the keepalive for this tick.
            _logger.LogDebug(
                "Advisory lock connection for schema {Schema} was busy; reporting the last known state of lock {LockId} without pinging",
                _schemaName, lockId);
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
    /// The GH-2602 liveness ping. Callers MUST hold <c>_gate</c> -- it does synchronous I/O on a held
    /// connection and may tear that connection down.
    /// </summary>
    private bool hasLockUnsafe(int lockId)
    {
        if (!holdsId(lockId)) return false;
        if (!tryFindHeld(lockId, out var held)) return false;

        // Oracle row-level FOR UPDATE locks are tied to the transaction
        // that took them, which is in turn tied to the holding connection.
        // If the connection died (network drop, RAC failover, manual KILL
        // SESSION), the row lock evaporates server-side but our in-memory
        // state still claims it. Ping the held connection so we can detect
        // a broken backend and self-clean. See GH-2602.
        try
        {
            using var cmd = held.conn.CreateCommand();
            cmd.CommandText = "select 1 from dual";
            cmd.CommandTimeout = 2;
            cmd.ExecuteScalar();
            return true;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e,
                "Lost advisory-lock connection for lock {LockId} in schema {Schema}; clearing held state",
                lockId, _schemaName);

            forgetId(lockId);
            try
            {
                held.tx.Dispose();
            }
            catch
            {
                // already broken
            }
            try
            {
                held.conn.Dispose();
            }
            catch
            {
                // already broken
            }
            return false;
        }
    }

    private bool holdsId(int lockId)
    {
        lock (_locksLock) return _locks.Contains(lockId);
    }

    private int[] heldIds()
    {
        lock (_locksLock) return _locks.ToArray();
    }

    private bool tryFindHeld(int lockId, out (OracleConnection conn, OracleTransaction tx) held)
    {
        lock (_locksLock) return _heldLocks.TryGetValue(lockId, out held);
    }

    private void rememberId(int lockId, OracleConnection conn, OracleTransaction tx)
    {
        lock (_locksLock)
        {
            _locks.Add(lockId);
            _heldLocks[lockId] = (conn, tx);
        }
    }

    /// <summary>
    /// Drop a lock id and hand back the connection/transaction that was holding it, so the caller can
    /// tear those down knowing nobody else can still find them.
    /// </summary>
    private bool forgetId(int lockId, out (OracleConnection conn, OracleTransaction tx) held)
    {
        lock (_locksLock)
        {
            _locks.Remove(lockId);
            return _heldLocks.Remove(lockId, out held);
        }
    }

    private void forgetId(int lockId)
    {
        forgetId(lockId, out _);
    }

    public async Task<bool> TryAttainLockAsync(int lockId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);

        // GH-4261 follow-up. Only two paths out of the try below hand these off to somebody else: the
        // success path, where rememberId retains them, and the ORA-00054 contention path, which tears
        // them down itself. Every other failure -- a missing schema, expired credentials, a RAC node
        // going away -- used to fall through to the outer catch and drop the locals on the floor with
        // the connection still open and, past BeginTransactionAsync, a transaction open on it. Attains
        // run on the health-check tick, so a failure mode that persists leaks one connection per tick
        // until the pool is gone. Held in locals so the catch can finish what the try started.
        OracleConnection? conn = null;
        OracleTransaction? tx = null;

        try
        {
            // GH-4275. Idempotent against the renewal the heartbeat drives on every tick. Oracle holds its
            // row lock in an UNCOMMITTED transaction on a dedicated connection -- that is how the lock is
            // held -- so without this short-circuit the next attain opens a SECOND connection whose
            // SELECT ... FOR UPDATE NOWAIT is blocked by this node's OWN first transaction and raises
            // ORA-00054. TryAttainLeadershipLockAsync answered false for a lock this node holds, and
            // NodeAgentController reads that as lost leadership: a sitting Oracle leader called
            // stepDownAsync on the very next tick after being elected, every tick, and never reached
            // EvaluateAssignmentsAsync -- which is only on the true branch.
            //
            // hasLockUnsafe, not HasLock: we already hold _gate and SemaphoreSlim is not reentrant. It
            // pings the retained connection, so this still distinguishes "we hold it" from "our session
            // died and the row lock evaporated", which is the GH-2602 property that has to survive.
            // Postgres, SQL Server and SQLite all open with the same short-circuit.
            if (hasLockUnsafe(lockId))
            {
                return true;
            }

            conn = await _source.OpenConnectionAsync(token);

            // Ensure lock row exists
            await using var ensureCmd = conn.CreateCommand(
                $"MERGE INTO {_schemaName}.{LockTable.TableName} t " +
                "USING DUAL ON (t.lock_id = :lockId) " +
                "WHEN NOT MATCHED THEN INSERT (lock_id) VALUES (:lockId)");
            ensureCmd.With("lockId", lockId);

            try
            {
                await ensureCmd.ExecuteNonQueryAsync(token);
            }
            catch (OracleException)
            {
                // Race condition - another process may have inserted it
            }

            // Start a transaction to hold the row lock
            tx = (OracleTransaction)await conn.BeginTransactionAsync(token);

            await using var lockCmd = conn.CreateCommand(
                $"SELECT lock_id FROM {_schemaName}.{LockTable.TableName} WHERE lock_id = :lockId FOR UPDATE NOWAIT");
            lockCmd.Transaction = tx;
            lockCmd.With("lockId", lockId);

            try
            {
                await lockCmd.ExecuteScalarAsync(token);
                rememberId(lockId, conn, tx);
                return true;
            }
            catch (OracleException ex) when (ex.Number == 54) // ORA-00054: resource busy
            {
                await tx.RollbackAsync(token);
                await closeWithinBudgetAsync(conn).ConfigureAwait(false);
                await conn.DisposeAsync();
                return false;
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to attain advisory lock {LockId}", lockId);

            // Reached only when the lock was NOT attained: the success and ORA-00054 paths both return
            // from inside the try. If the ORA-00054 teardown was itself what threw, this finishes it --
            // discardFailedAttainAsync is defensive about being handed something already torn down.
            await discardFailedAttainAsync(lockId, conn, tx).ConfigureAwait(false);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// GH-4261 follow-up. Give back the connection -- and the transaction, if
    /// <c>BeginTransactionAsync</c> got that far -- that a failed attain opened. Callers MUST hold
    /// <c>_gate</c>, and MUST NOT call this once <see cref="rememberId"/> has taken ownership. Nothing
    /// in here is allowed to throw: it runs from a catch block whose whole job is to answer false.
    /// </summary>
    private async Task discardFailedAttainAsync(int lockId, OracleConnection? conn, OracleTransaction? tx)
    {
        if (conn == null) return;

        if (tx != null)
        {
            try
            {
                // Bounded like releaseLockUnsafeAsync's rollback, and for the same reason: we are
                // already on a failure path and cannot afford a second unbounded wait on a connection
                // that has just misbehaved.
                using var cancellation = new CancellationTokenSource();
                cancellation.CancelAfter(1.Seconds());

                await tx.RollbackAsync(cancellation.Token);
            }
            catch (Exception e)
            {
                _logger.LogDebug(e,
                    "Error rolling back the transaction of a failed attain of advisory lock {LockId} in schema {Schema}",
                    lockId, _schemaName);
            }

            try
            {
                await tx.DisposeAsync();
            }
            catch
            {
                // Already broken, and the connection is going with it regardless.
            }
        }

        try
        {
            await closeWithinBudgetAsync(conn).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e,
                "Error closing the connection of a failed attain of advisory lock {LockId} in schema {Schema}",
                lockId, _schemaName);
        }

        try
        {
            await conn.DisposeAsync();
        }
        catch (Exception e)
        {
            _logger.LogDebug(e,
                "Error disposing the connection of a failed attain of advisory lock {LockId} in schema {Schema}",
                lockId, _schemaName);
        }
    }

    public async Task ReleaseLockAsync(int lockId)
    {
        if (!holdsId(lockId)) return;

        // GH-4261: bounded rather than indefinite. If the gate is still held past the budget something
        // is wedged on the connection, and waiting longer turns a released lock into a hung shutdown.
        // Oracle drops the row lock when the holding session ends, so an abandoned lock clears itself
        // when this process exits.
        if (!await _gate.WaitAsync(CloseBudget).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "Timed out waiting to release advisory lock {LockId} in schema {Schema}; leaving it to be released when the session ends",
                lockId, _schemaName);
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
        if (!forgetId(lockId, out var held)) return;

        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(1.Seconds());

            await held.tx.RollbackAsync(cancellation.Token);
            await closeWithinBudgetAsync(held.conn).ConfigureAwait(false);
            await held.conn.DisposeAsync();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error releasing advisory lock {LockId}", lockId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!await _gate.WaitAsync(CloseBudget).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "Timed out waiting to dispose the advisory locks for schema {Schema}; abandoning them",
                _schemaName);
            return;
        }

        try
        {
            foreach (var lockId in heldIds())
            {
                if (!forgetId(lockId, out var held)) continue;

                try
                {
                    await held.tx.RollbackAsync(CancellationToken.None);
                    await closeWithinBudgetAsync(held.conn).ConfigureAwait(false);
                    await held.conn.DisposeAsync();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error disposing advisory lock {LockId}", lockId);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// GH-4261. <see cref="OracleConnection.CloseAsync"/> takes no <see cref="CancellationToken"/>, so a
    /// caller's shutdown budget cannot reach it. The gate above should stop a connection ever getting
    /// into a state where the close does not return; this makes it survivable if one ever does. Returns
    /// false when the close was abandoned -- the caller's DisposeAsync then runs against a connection
    /// whose close never completed, which the surrounding catch absorbs.
    /// </summary>
    private async Task<bool> closeWithinBudgetAsync(OracleConnection conn)
    {
        var closing = conn.CloseAsync();

        using var delay = new CancellationTokenSource();
        var finished = await Task.WhenAny(closing, Task.Delay(CloseBudget, delay.Token)).ConfigureAwait(false);
        await delay.CancelAsync().ConfigureAwait(false);

        if (!ReferenceEquals(finished, closing))
        {
            _logger.LogWarning(
                "Timed out after {Budget} closing an advisory lock connection for schema {Schema}; abandoning it",
                CloseBudget, _schemaName);

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
