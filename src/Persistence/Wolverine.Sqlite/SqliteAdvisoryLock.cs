using System.Data;
using System.Data.Common;
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
        if (_conn is null) return false;
        if (!_locks.Contains(lockId)) return false;

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
                _databaseName, _locks);

            _locks.Clear();
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

    public async Task<bool> TryAttainLockAsync(int lockId, CancellationToken token)
    {
        // Idempotent: if we already hold this lock and the connection is healthy,
        // re-attempting must report success. The previous implementation would run
        // INSERT OR IGNORE again, get result==0, and falsely return false.
        if (HasLock(lockId))
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
                _locks.Add(lockId);
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
        if (!_locks.Contains(lockId))
        {
            return;
        }

        if (_conn == null || _conn.State == ConnectionState.Closed)
        {
            _locks.Remove(lockId);
            return;
        }

        try
        {
            await _conn.CreateCommand("DELETE FROM wolverine_locks WHERE lock_id = @lockId")
                .With("lockId", lockId)
                .ExecuteNonQueryAsync();
            _locks.Remove(lockId);

            if (!_locks.Any())
            {
                await _conn.CloseAsync().ConfigureAwait(false);
                await _conn.DisposeAsync().ConfigureAwait(false);
                _conn = null;
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

        try
        {
            foreach (var lockId in _locks.ToList())
            {
                await ReleaseLockAsync(lockId);
            }

            // ReleaseLockAsync nulls _conn once the last lock is released. The
            // finally block below handles disposal in both paths (released-all
            // vs released-some); calling Close/Dispose here as well caused a
            // NullReferenceException on the all-released path.
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to dispose of advisory locks for database {Identifier}",
                _databaseName);
        }
        finally
        {
            if (_conn != null)
            {
                await _conn.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
