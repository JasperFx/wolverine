using System.Data;
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
        if (_conn is null) return false;
        if (!_locks.Contains(lockId)) return false;

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
            _locks.Add(lockId);
            return true;
        }

        // For MySQL 8.0+, result might be long
        if (result is long longResult && longResult == 1)
        {
            _locks.Add(lockId);
            return true;
        }

        return false;
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

        var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(1.Seconds());

        var lockName = ToLockName(lockId);

        await using var cmd = _conn.CreateCommand();
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

        _locks.Remove(lockId);

        if (!_locks.Any())
        {
            await _conn.CloseAsync().ConfigureAwait(false);
            await _conn.DisposeAsync().ConfigureAwait(false);
            _conn = null;
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
            foreach (var lockId in _locks)
            {
                var lockName = ToLockName(lockId);

                await using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT RELEASE_LOCK(@lockName)";
                cmd.Parameters.AddWithValue("@lockName", lockName);

                await cmd.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>
    /// Convert integer lock ID to a MySQL lock name string.
    /// MySQL lock names are limited to 64 characters.
    /// </summary>
    private static string ToLockName(int lockId)
    {
        return $"wolverine_{lockId}";
    }
}
