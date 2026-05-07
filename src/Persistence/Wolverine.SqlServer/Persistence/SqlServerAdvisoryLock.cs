using System.Data;
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
        if (_conn is null) return false;
        if (!_locks.Contains(lockId)) return false;

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
        if (_locks.Contains(lockId) && HasLock(lockId))
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
            _locks.Add(lockId);
            return true;
        }

        return false;
    }

    public async Task ReleaseLockAsync(int lockId)
    {
        if (!_locks.Contains(lockId)) return;

        if (_conn == null || _conn.State == ConnectionState.Closed)
        {
            _locks.Remove(lockId);
            return;
        }

        try
        {
            var cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(1.Seconds());

            await _conn.ReleaseGlobalLock(lockId.ToString(), cancellation: cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e,
                "Error trying to release advisory lock {LockId} for database {Identifier}",
                lockId, _databaseName);
        }

        _locks.Remove(lockId);

        if (!_locks.Any())
        {
            await safeCloseConnectionAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_conn == null) return;

        try
        {
            if (_conn.State == ConnectionState.Open)
            {
                foreach (var i in _locks)
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

    private async Task safeCloseConnectionAsync()
    {
        if (_conn == null) return;

        try
        {
            if (_conn.State == ConnectionState.Open)
            {
                await _conn.CloseAsync().ConfigureAwait(false);
            }

            await _conn.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Error trying to close advisory lock connection for database {Identifier}",
                _databaseName);
        }
        finally
        {
            _conn = null;
        }
    }
}
