using System.Data;
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
        return _locks.Contains(lockId);
    }

    public async Task<bool> TryAttainLockAsync(int lockId, CancellationToken token)
    {
        try
        {
            var conn = await _source.OpenConnectionAsync(token);

            // Ensure lock row exists
            var ensureCmd = conn.CreateCommand(
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
            var tx = (OracleTransaction)await conn.BeginTransactionAsync(token);

            var lockCmd = conn.CreateCommand(
                $"SELECT lock_id FROM {_schemaName}.{LockTable.TableName} WHERE lock_id = :lockId FOR UPDATE NOWAIT");
            lockCmd.Transaction = tx;
            lockCmd.With("lockId", lockId);

            try
            {
                await lockCmd.ExecuteScalarAsync(token);
                _locks.Add(lockId);
                _heldLocks[lockId] = (conn, tx);
                return true;
            }
            catch (OracleException ex) when (ex.Number == 54) // ORA-00054: resource busy
            {
                await tx.RollbackAsync(token);
                await conn.CloseAsync();
                await conn.DisposeAsync();
                return false;
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to attain advisory lock {LockId}", lockId);
            return false;
        }
    }

    public async Task ReleaseLockAsync(int lockId)
    {
        if (!_locks.Contains(lockId)) return;

        _locks.Remove(lockId);

        if (_heldLocks.TryGetValue(lockId, out var held))
        {
            _heldLocks.Remove(lockId);
            try
            {
                var cancellation = new CancellationTokenSource();
                cancellation.CancelAfter(1.Seconds());

                await held.tx.RollbackAsync(cancellation.Token);
                await held.conn.CloseAsync();
                await held.conn.DisposeAsync();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error releasing advisory lock {LockId}", lockId);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lockId in _locks.ToArray())
        {
            if (_heldLocks.TryGetValue(lockId, out var held))
            {
                try
                {
                    await held.tx.RollbackAsync(CancellationToken.None);
                    await held.conn.CloseAsync();
                    await held.conn.DisposeAsync();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error disposing advisory lock {LockId}", lockId);
                }
            }
        }

        _locks.Clear();
        _heldLocks.Clear();
    }
}
