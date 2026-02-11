using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;
using Weasel.Oracle;
using Wolverine.Oracle.Util;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace Wolverine.Oracle;

internal partial class OracleMessageStore
{
    public async Task PollForScheduledMessagesAsync(IWolverineRuntime runtime, ILogger logger,
        DurabilitySettings durabilitySettings, CancellationToken cancellationToken)
    {
        if (HasDisposed) return;

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            var tx = (OracleTransaction)await conn.BeginTransactionAsync(cancellationToken);

            // Try to attain a row-level lock for scheduled jobs
            var lockCmd = conn.CreateCommand(
                $"SELECT lock_id FROM {SchemaName}.{Schema.LockTable.TableName} WHERE lock_id = :lockId FOR UPDATE NOWAIT");
            lockCmd.Transaction = tx;
            lockCmd.With("lockId", _settings.ScheduledJobLockId);

            bool gotLock;
            try
            {
                await lockCmd.ExecuteScalarAsync(cancellationToken);
                gotLock = true;
            }
            catch (OracleException ex) when (ex.Number == 54) // ORA-00054: resource busy
            {
                gotLock = false;
                await tx.RollbackAsync(cancellationToken);
            }

            if (gotLock)
            {
                var builder = ToOracleCommandBuilder();
                builder.Append(
                    $"SELECT {DatabaseConstants.IncomingFields} FROM {SchemaName}.{DatabaseConstants.IncomingTable} WHERE status = '{EnvelopeStatus.Scheduled}' AND execution_time <= ");
                builder.AppendParameter(DateTimeOffset.UtcNow);
                builder.Append($" ORDER BY execution_time FETCH FIRST {_durability.RecoveryBatchSize} ROWS ONLY");
                var cmd = builder.Compile();
                cmd.Connection = conn;
                cmd.Transaction = tx;

                var envelopes = await cmd.FetchListAsync(
                    reader => OracleEnvelopeReader.ReadIncomingAsync(reader, cancellationToken), cancellationToken);

                if (envelopes.Count == 0)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return;
                }

                var ids = envelopes.Select(x => x.Id).ToArray();
                var reassignCmd = conn.CreateCommand("");
                reassignCmd.Transaction = tx;
                var placeholders = OracleCommandExtensions.WithIdList(reassignCmd, "id", ids);
                reassignCmd.CommandText =
                    $"UPDATE {SchemaName}.{DatabaseConstants.IncomingTable} SET owner_id = :owner, status = '{EnvelopeStatus.Incoming}' WHERE id IN ({placeholders})";
                reassignCmd.With("owner", durabilitySettings.AssignedNodeNumber);
                await reassignCmd.ExecuteNonQueryAsync(_cancellation);

                await tx.CommitAsync(cancellationToken);

                await runtime.EnqueueDirectlyAsync(envelopes);
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
