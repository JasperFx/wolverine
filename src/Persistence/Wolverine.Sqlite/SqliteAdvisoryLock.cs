using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace Wolverine.Sqlite;

internal class SqliteAdvisoryLock : IAdvisoryLock
{
    private readonly DbDataSource _dataSource;
    private readonly ILogger _logger;
    private readonly string _databaseName;
    private readonly List<int> _locks = new();
    private DbConnection? _conn;

    public SqliteAdvisoryLock(DbDataSource dataSource, ILogger logger, string databaseName)
    {
        _dataSource = dataSource;
        _logger = logger;
        _databaseName = databaseName;
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
            // SQLite doesn't have advisory locks like PostgreSQL
            // We'll use a simple table-based lock approach
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

            await _conn.CloseAsync().ConfigureAwait(false);
            await _conn.DisposeAsync().ConfigureAwait(false);
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
