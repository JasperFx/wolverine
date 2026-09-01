using System.Data.Common;
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
            await using var lockCmd = conn.CreateCommand(
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
                await using var cmd = builder.Compile();
                cmd.Connection = conn;
                cmd.Transaction = tx;

                var envelopes = await cmd.FetchListAsync(
                    reader => OracleEnvelopeReader.ReadIncomingAsync(reader, cancellationToken), cancellationToken);

                if (envelopes.Count == 0)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return;
                }

                await using var reassignCmd = conn.CreateCommand("");
                reassignCmd.Transaction = tx;
                reassignCmd.CommandText = writePromotionSql(reassignCmd, envelopes);
                reassignCmd.With("owner", durabilitySettings.AssignedNodeNumber);
                await reassignCmd.ExecuteNonQueryAsync(_cancellation);

                await tx.CommitAsync(cancellationToken);

                // Stamp owning store on each row so downstream pipeline routes
                // its writes back here. See GH-2576.
                foreach (var envelope in envelopes)
                {
                    envelope.Store = this;
                }

                await runtime.EnqueueDirectlyAsync(envelopes);
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    /// <summary>
    /// GH-4216. Builds the promotion statement for the batch the poller just selected. Two things it must
    /// not do, both of which the previous <c>WHERE id IN (...)</c> did:
    ///
    /// <list type="bullet">
    /// <item>Under <see cref="MessageIdentity.IdAndDestination"/> the identity is <c>(id, received_at)</c>, so
    /// matching on the id alone also rewrites a row for a different message that merely shares the id at
    /// another destination -- reassigning an <c>owner_id</c> the poller never selected. The pairs have to be
    /// matched together: an <c>id IN (...) AND received_at IN (...)</c> pair of lists would match the cross
    /// product, which is the same bug wearing a longer statement.</item>
    /// <item>Without a status predicate it also rewrites rows that are already Incoming or Handled, and
    /// promotes a scheduled sibling that is not due yet -- a message scheduled an hour out executes now. The
    /// load side already selects only Scheduled rows, so constraining the update to match costs nothing.</item>
    /// </list>
    ///
    /// Mirrors <c>PostgresqlMessageStore._reassignIncomingSql</c>, which does the same job through
    /// <c>unnest(@ids, @uris)</c>. See GH-4209 for the incident that first exposed the shape. Parameters are
    /// bound by name here -- Weasel's Oracle commands set <c>BindByName</c> -- so the order they are added in
    /// does not have to match the order they appear in the statement.
    /// </summary>
    private string writePromotionSql(DbCommand command, IReadOnlyList<Envelope> envelopes)
    {
        var matchesById = _durability.MessageIdentity == MessageIdentity.IdOnly;
        var clauses = new List<string>(envelopes.Count);

        for (var i = 0; i < envelopes.Count; i++)
        {
            var idParameter = command.CreateParameter();
            idParameter.ParameterName = $"id_{i}";
            // Oracle stores the id as RAW(16), so it binds as bytes rather than as a Guid.
            idParameter.Value = envelopes[i].Id.ToByteArray();
            command.Parameters.Add(idParameter);

            if (matchesById)
            {
                clauses.Add($"id = :id_{i}");
                continue;
            }

            var uriParameter = command.CreateParameter();
            uriParameter.ParameterName = $"uri_{i}";
            uriParameter.Value = envelopes[i].Destination!.ToString();
            command.Parameters.Add(uriParameter);

            clauses.Add($"(id = :id_{i} AND {DatabaseConstants.ReceivedAt} = :uri_{i})");
        }

        return $"UPDATE {SchemaName}.{DatabaseConstants.IncomingTable} SET owner_id = :owner, status = '{EnvelopeStatus.Incoming}' " +
               $"WHERE status = '{EnvelopeStatus.Scheduled}' AND ({string.Join(" OR ", clauses)})";
    }
}
