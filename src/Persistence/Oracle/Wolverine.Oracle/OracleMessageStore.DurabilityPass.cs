using System.Text;
using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;
using Weasel.Oracle;
using Wolverine.Oracle.Schema;
using Wolverine.Oracle.Util;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Oracle;

/// <summary>
///     GH-5016. Oracle's part in an externally triggered durability pass under
///     <see cref="DurabilityMode.ServerlessWithOutbox" />
/// </summary>
internal partial class OracleMessageStore : IMessageStoreWithDurabilityPass, IStaleOutgoingSource,
    IDurabilityPassInboxSource
{
    /// <summary>
    ///     The row in this schema's lock table whose <c>FOR UPDATE NOWAIT</c> lock keeps two overlapping durability
    ///     passes against the same message schema from sending the same rows twice. The same row-lock mechanism
    ///     as <see cref="OracleAdvisoryLock" />, because Oracle has no advisory locks without DBMS_LOCK grants
    /// </summary>
    internal int DurabilityPassLockId => $"{SchemaName}:durability-pass".GetDeterministicHashCode();

    public async Task<IAsyncDisposable?> TryAcquireDurabilityPassLockAsync(CancellationToken token)
    {
        // A dedicated connection for as long as the pass runs. The row lock belongs to the open transaction
        // on it, so it cannot go back to the pool until the pass is done
        var conn = await _dataSource.OpenConnectionAsync(token);
        OracleTransaction? tx = null;

        try
        {
            await using (var ensureCmd = conn.CreateCommand(
                             $"MERGE INTO {SchemaName}.{LockTable.TableName} t " +
                             "USING DUAL ON (t.lock_id = :lockId) " +
                             "WHEN NOT MATCHED THEN INSERT (lock_id) VALUES (:lockId)"))
            {
                ensureCmd.With("lockId", DurabilityPassLockId);

                try
                {
                    await ensureCmd.ExecuteNonQueryAsync(token);
                }
                catch (OracleException e) when (e.Number == 1)
                {
                    // Another pass inserted the row first, which is all this needed
                }
            }

            tx = (OracleTransaction)await conn.BeginTransactionAsync(token);

            await using var lockCmd = conn.CreateCommand(
                $"SELECT lock_id FROM {SchemaName}.{LockTable.TableName} WHERE lock_id = :lockId FOR UPDATE NOWAIT");
            lockCmd.Transaction = tx;
            lockCmd.With("lockId", DurabilityPassLockId);

            try
            {
                await lockCmd.ExecuteScalarAsync(token);
                return new DurabilityPassLock(conn, tx);
            }
            catch (OracleException e) when (e.Number == 54) // ORA-00054: resource busy
            {
                await tx.RollbackAsync(token);
                await tx.DisposeAsync();
                await conn.DisposeAsync();
                return null;
            }
        }
        catch
        {
            if (tx != null) await tx.DisposeAsync();
            await conn.DisposeAsync();
            throw;
        }
    }

    public IReadOnlyList<IDurabilityPassStep> BuildDurabilityPassSteps(IWolverineRuntime runtime)
    {
        return
        [
            new RecoverStaleOutgoingStep(this, this, runtime.LoggerFactory.CreateLogger<RecoverStaleOutgoingStep>()),
            new ReleaseScheduledMessagesStep(this, this, runtime.LoggerFactory.CreateLogger<ReleaseScheduledMessagesStep>()),
            new RecoverIncomingMessagesStep(this, this, runtime.LoggerFactory.CreateLogger<RecoverIncomingMessagesStep>()),
            new DeleteExpiredRowsStep(this, runtime.LoggerFactory.CreateLogger<DeleteExpiredRowsStep>())
        ];
    }

    public async Task<IReadOnlyList<Envelope>> LoadStaleOutgoingAsync(DateTimeOffset? staleBefore, Guid[] excluded,
        int limit, CancellationToken token)
    {
        if (HasDisposed) return [];

        await using var conn = await _dataSource.OpenConnectionAsync(token);
        await using var cmd = conn.CreateCommand("");

        var sql = new StringBuilder(
            $"SELECT {DatabaseConstants.OutgoingFields} FROM {SchemaName}.{DatabaseConstants.OutgoingTable}");
        writeStaleOutgoingPredicate(cmd, sql, staleBefore, excluded);

        // Oldest first wherever the stale clock exists
        if (_durability.OutboxStaleTime.HasValue)
        {
            sql.Append($" ORDER BY {DatabaseConstants.Timestamp}");
        }

        sql.Append($" FETCH FIRST {limit} ROWS ONLY");
        cmd.CommandText = sql.ToString();

        return await cmd.FetchListAsync(r => OracleEnvelopeReader.ReadOutgoingAsync(r, token), token);
    }

    public async Task<int> CountStaleOutgoingAsync(DateTimeOffset? staleBefore, Guid[] excluded,
        CancellationToken token)
    {
        if (HasDisposed) return 0;

        await using var conn = await _dataSource.OpenConnectionAsync(token);
        await using var cmd = conn.CreateCommand("");

        var sql = new StringBuilder($"SELECT COUNT(*) FROM {SchemaName}.{DatabaseConstants.OutgoingTable}");
        writeStaleOutgoingPredicate(cmd, sql, staleBefore, excluded);
        cmd.CommandText = sql.ToString();

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(token));
    }

    // owner_id = 0 [and timestamp <= :stale] [and id not in (:excluded)]
    private void writeStaleOutgoingPredicate(OracleCommand cmd, StringBuilder sql, DateTimeOffset? staleBefore,
        Guid[] excluded)
    {
        sql.Append($" WHERE {DatabaseConstants.OwnerId} = {TransportConstants.AnyNode}");

        // The timestamp column only exists when OutboxStaleTime is set
        if (staleBefore.HasValue && _durability.OutboxStaleTime.HasValue)
        {
            sql.Append($" AND {DatabaseConstants.Timestamp} <= :staleBefore");
            addTimestamp(cmd, "staleBefore", staleBefore.Value);
        }

        writeExcluded(cmd, sql, excluded);
    }

    public async Task<IReadOnlyList<Envelope>> LoadDueScheduledAsync(DateTimeOffset now, Guid[] excluded, int limit,
        CancellationToken token)
    {
        if (HasDisposed) return [];

        await using var conn = await _dataSource.OpenConnectionAsync(token);
        await using var cmd = conn.CreateCommand("");

        var sql = new StringBuilder(
            $"SELECT {DatabaseConstants.IncomingFields} FROM {SchemaName}.{DatabaseConstants.IncomingTable}");
        writeDueScheduledPredicate(cmd, sql, now, excluded);
        sql.Append($" ORDER BY {DatabaseConstants.ExecutionTime} FETCH FIRST {limit} ROWS ONLY");
        cmd.CommandText = sql.ToString();

        return await loadIncomingAsync(cmd, token);
    }

    public async Task<int> CountDueScheduledAsync(DateTimeOffset now, Guid[] excluded, CancellationToken token)
    {
        if (HasDisposed) return 0;

        await using var conn = await _dataSource.OpenConnectionAsync(token);
        await using var cmd = conn.CreateCommand("");

        var sql = new StringBuilder($"SELECT COUNT(*) FROM {SchemaName}.{DatabaseConstants.IncomingTable}");
        writeDueScheduledPredicate(cmd, sql, now, excluded);
        cmd.CommandText = sql.ToString();

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(token));
    }

    public async Task<IReadOnlyList<Envelope>> LoadRecoverableIncomingAsync(DateTimeOffset? staleBefore,
        Guid[] excluded, int limit, CancellationToken token)
    {
        if (HasDisposed) return [];

        await using var conn = await _dataSource.OpenConnectionAsync(token);
        await using var cmd = conn.CreateCommand("");

        var sql = new StringBuilder(
            $"SELECT {DatabaseConstants.IncomingFields} FROM {SchemaName}.{DatabaseConstants.IncomingTable}");
        writeRecoverableIncomingPredicate(cmd, sql, staleBefore, excluded);
        sql.Append($" FETCH FIRST {limit} ROWS ONLY");
        cmd.CommandText = sql.ToString();

        return await loadIncomingAsync(cmd, token);
    }

    public async Task<int> CountRecoverableIncomingAsync(DateTimeOffset? staleBefore, Guid[] excluded,
        CancellationToken token)
    {
        if (HasDisposed) return 0;

        await using var conn = await _dataSource.OpenConnectionAsync(token);
        await using var cmd = conn.CreateCommand("");

        var sql = new StringBuilder($"SELECT COUNT(*) FROM {SchemaName}.{DatabaseConstants.IncomingTable}");
        writeRecoverableIncomingPredicate(cmd, sql, staleBefore, excluded);
        cmd.CommandText = sql.ToString();

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(token));
    }

    // status = 'Scheduled' and execution_time <= :now [and id not in (:excluded)]
    private static void writeDueScheduledPredicate(OracleCommand cmd, StringBuilder sql, DateTimeOffset now,
        Guid[] excluded)
    {
        sql.Append(
            $" WHERE {DatabaseConstants.Status} = '{EnvelopeStatus.Scheduled}' AND {DatabaseConstants.ExecutionTime} <= :now");
        addTimestamp(cmd, "now", now);
        writeExcluded(cmd, sql, excluded);
    }

    // status = 'Incoming' and (timestamp <= :stale | owner_id = 0) [and id not in (:excluded)]
    private void writeRecoverableIncomingPredicate(OracleCommand cmd, StringBuilder sql, DateTimeOffset? staleBefore,
        Guid[] excluded)
    {
        sql.Append($" WHERE {DatabaseConstants.Status} = '{EnvelopeStatus.Incoming}'");

        // The timestamp column only exists when InboxStaleTime is set
        if (staleBefore.HasValue && _durability.InboxStaleTime.HasValue)
        {
            sql.Append($" AND {DatabaseConstants.Timestamp} <= :staleBefore");
            addTimestamp(cmd, "staleBefore", staleBefore.Value);
        }
        else
        {
            sql.Append($" AND {DatabaseConstants.OwnerId} = {TransportConstants.AnyNode}");
        }

        writeExcluded(cmd, sql, excluded);
    }

    private static void addTimestamp(OracleCommand cmd, string name, DateTimeOffset value)
    {
        cmd.Parameters.Add(new OracleParameter(name, OracleDbType.TimeStampTZ) { Value = value });
    }

    // One RAW(16) bind per excluded id. Only rows that already failed in this pass are excluded, which keeps
    // the list far below Oracle's 1,000 expression ceiling for an IN list
    private static void writeExcluded(OracleCommand cmd, StringBuilder sql, Guid[] excluded)
    {
        if (excluded.Length == 0) return;

        sql.Append($" AND {DatabaseConstants.Id} NOT IN ({cmd.WithIdList("excluded", excluded)})");
    }

    private async Task<IReadOnlyList<Envelope>> loadIncomingAsync(OracleCommand cmd, CancellationToken token)
    {
        var envelopes = await cmd.FetchListAsync(r => OracleEnvelopeReader.ReadIncomingAsync(r, token), token);
        foreach (var envelope in envelopes) envelope.Store = this;

        return envelopes;
    }

    private sealed class DurabilityPassLock : IAsyncDisposable
    {
        private readonly OracleConnection _conn;
        private readonly OracleTransaction _tx;

        public DurabilityPassLock(OracleConnection conn, OracleTransaction tx)
        {
            _conn = conn;
            _tx = tx;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _tx.RollbackAsync(CancellationToken.None);
            }
            finally
            {
                // Ending the session releases the row lock no matter what
                await _tx.DisposeAsync();
                await _conn.DisposeAsync();
            }
        }
    }
}
