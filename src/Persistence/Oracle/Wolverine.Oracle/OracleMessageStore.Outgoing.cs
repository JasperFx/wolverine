using System.Data.Common;
using System.Text;
using Oracle.ManagedDataAccess.Client;
using Weasel.Oracle;
using Wolverine.Oracle.Util;
using Wolverine.RDBMS;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.Oracle;

internal partial class OracleMessageStore
{
    public async Task<IReadOnlyList<Envelope>> LoadOutgoingAsync(Uri destination)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);
        await using var cmd = conn.CreateCommand(
            $"SELECT {DatabaseConstants.OutgoingFields} FROM {SchemaName}.{DatabaseConstants.OutgoingTable} " +
            $"WHERE owner_id = {TransportConstants.AnyNode} AND destination = :destination " +
            $"FETCH FIRST {_durability.RecoveryBatchSize} ROWS ONLY");
        cmd.With("destination", destination.ToString());

        var list = await cmd.FetchListAsync(r => OracleEnvelopeReader.ReadOutgoingAsync(r), _cancellation);
        await conn.CloseAsync();
        return list;
    }

    public async Task StoreOutgoingAsync(Envelope envelope, int ownerId)
    {
        var data = EnvelopeSerializer.Serialize(envelope);

        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);
        await using var cmd = conn.CreateCommand(
            $"INSERT INTO {SchemaName}.{DatabaseConstants.OutgoingTable} ({DatabaseConstants.OutgoingFields}) " +
            "VALUES (:body, :id, :ownerId, :destination, :deliverBy, :attempts, :messageType)");

        cmd.Parameters.Add(new OracleParameter("body", OracleDbType.Blob) { Value = data });
        cmd.With("id", envelope.Id);
        cmd.With("ownerId", ownerId);
        cmd.With("destination", envelope.Destination!.ToString());
        cmd.Parameters.Add(new OracleParameter("deliverBy", OracleDbType.TimeStampTZ) { Value = (object?)envelope.DeliverBy ?? DBNull.Value });
        cmd.With("attempts", envelope.Attempts);
        cmd.With("messageType", envelope.MessageType!);

        try
        {
            await cmd.ExecuteNonQueryAsync(_cancellation);
        }
        catch (OracleException e) when (e.Number == 1)
        {
            // Idempotent -- the row is in the outgoing table either way, which is what the flag below
            // reports, so a duplicate insert still counts as persisted
        }
        finally
        {
            await conn.CloseAsync();
        }

        // GH-4371. OracleQueueSender.SendAsync branches on this to decide between MOVING the row from
        // outgoing into the queue table in one transaction and writing the queue row separately. Oracle
        // never set it, so the move branch was unreachable and every durable send took the two-step
        // route -- where a crash between the queue insert and the outgoing delete leaves the message in
        // both, and the durability agent sends it again. Every MessageDatabase<T> store has always set
        // this; Oracle alone did not.
        envelope.WasPersistedInOutbox = true;
    }

    /// <summary>
    /// GH-4369. One <c>INSERT ALL</c> for the whole batch instead of one INSERT per envelope --
    /// deliberately not Oracle array binding, which does not carry a BLOB bind as cleanly and turns a
    /// duplicate row into a partial-apply that has to be unpicked.
    ///
    /// <para>
    /// An <c>INSERT ALL</c> is one statement, so ORA-00001 fails the whole batch and nothing is
    /// written. That is the right shape: the caller's coalescer then stores each envelope on its own,
    /// where <see cref="StoreOutgoingAsync(Envelope, int)" />'s existing ORA-00001 swallow makes the
    /// duplicate idempotent exactly as it was before this overload existed. The batch is a fast path,
    /// never the only path.
    /// </para>
    /// </summary>
    public async Task StoreOutgoingAsync(IReadOnlyList<Envelope> envelopes, int ownerId)
    {
        if (HasDisposed || envelopes.Count == 0) return;

        if (envelopes.Count == 1)
        {
            await StoreOutgoingAsync(envelopes[0], ownerId);
            return;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);
        await using var cmd = conn.CreateCommand("");

        var sql = new StringBuilder("INSERT ALL");
        for (var i = 0; i < envelopes.Count; i++)
        {
            var envelope = envelopes[i];

            sql.Append($" INTO {SchemaName}.{DatabaseConstants.OutgoingTable} ({DatabaseConstants.OutgoingFields}) ")
                .Append($"VALUES (:body_{i}, :id_{i}, :ownerId_{i}, :destination_{i}, :deliverBy_{i}, :attempts_{i}, :messageType_{i})");

            cmd.Parameters.Add(new OracleParameter($"body_{i}", OracleDbType.Blob)
            {
                Value = EnvelopeSerializer.Serialize(envelope)
            });
            cmd.With($"id_{i}", envelope.Id);
            cmd.With($"ownerId_{i}", ownerId);
            cmd.With($"destination_{i}", envelope.Destination!.ToString());
            cmd.Parameters.Add(new OracleParameter($"deliverBy_{i}", OracleDbType.TimeStampTZ)
            {
                Value = (object?)envelope.DeliverBy ?? DBNull.Value
            });
            cmd.With($"attempts_{i}", envelope.Attempts);
            cmd.With($"messageType_{i}", envelope.MessageType!);
        }

        // INSERT ALL is a multi-table insert and needs a driving query; SELECT 1 FROM dual runs it once
        sql.Append(" SELECT 1 FROM dual");
        cmd.CommandText = sql.ToString();

        try
        {
            await cmd.ExecuteNonQueryAsync(_cancellation);
        }
        finally
        {
            await conn.CloseAsync();
        }

        // GH-4371 settled the question this deliberately left open: Oracle does stamp the flag, on
        // every store path, exactly as MessageDatabase<T> does. A coalesced send has to reach the same
        // move branch a single send reaches.
        foreach (var envelope in envelopes)
        {
            envelope.WasPersistedInOutbox = true;
        }
    }

    public async Task DeleteOutgoingAsync(Envelope[] envelopes)
    {
        if (HasDisposed) return;
        if (envelopes.Length == 0) return;

        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);
        await using var cmd = conn.CreateCommand("");
        var placeholders = OracleCommandExtensions.WithEnvelopeIds(cmd, "id", envelopes);
        cmd.CommandText = $"DELETE FROM {SchemaName}.{DatabaseConstants.OutgoingTable} WHERE id IN ({placeholders})";
        await cmd.ExecuteNonQueryAsync(_cancellation);
        await conn.CloseAsync();
    }

    public async Task DeleteOutgoingAsync(Envelope envelope)
    {
        if (HasDisposed) return;

        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);
        await using var cmd = conn.CreateCommand(
            $"DELETE FROM {SchemaName}.{DatabaseConstants.OutgoingTable} WHERE id = :id");
        cmd.With("id", envelope.Id);
        await cmd.ExecuteNonQueryAsync(_cancellation);
        await conn.CloseAsync();
    }

    public async Task DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);

        try
        {
            if (discards.Length > 0)
            {
                await using var deleteCmd = conn.CreateCommand("");
                var deletePlaceholders = OracleCommandExtensions.WithEnvelopeIds(deleteCmd, "id", discards);
                deleteCmd.CommandText =
                    $"DELETE FROM {SchemaName}.{DatabaseConstants.OutgoingTable} WHERE id IN ({deletePlaceholders})";
                await deleteCmd.ExecuteNonQueryAsync(_cancellation);
            }

            if (reassigned.Length > 0)
            {
                await using var reassignCmd = conn.CreateCommand("");
                var reassignPlaceholders = OracleCommandExtensions.WithEnvelopeIds(reassignCmd, "rid", reassigned);
                reassignCmd.CommandText =
                    $"UPDATE {SchemaName}.{DatabaseConstants.OutgoingTable} SET owner_id = :node WHERE id IN ({reassignPlaceholders})";
                reassignCmd.With("node", nodeId);
                await reassignCmd.ExecuteNonQueryAsync(_cancellation);
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    // IMessageDatabase
    public async Task StoreOutgoingAsync(DbTransaction tx, Envelope[] envelopes)
    {
        foreach (var envelope in envelopes)
        {
            var data = EnvelopeSerializer.Serialize(envelope);

            await using var cmd = ((OracleConnection)tx.Connection!).CreateCommand(
                $"INSERT INTO {SchemaName}.{DatabaseConstants.OutgoingTable} ({DatabaseConstants.OutgoingFields}) " +
                "VALUES (:body, :id, :ownerId, :destination, :deliverBy, :attempts, :messageType)");
            cmd.Transaction = (OracleTransaction)tx;

            cmd.Parameters.Add(new OracleParameter("body", OracleDbType.Blob) { Value = data });
            cmd.With("id", envelope.Id);
            cmd.With("ownerId", envelope.OwnerId);
            cmd.With("destination", envelope.Destination!.ToString());
            cmd.Parameters.Add(new OracleParameter("deliverBy", OracleDbType.TimeStampTZ) { Value = (object?)envelope.DeliverBy ?? DBNull.Value });
            cmd.With("attempts", envelope.Attempts);
            cmd.With("messageType", envelope.MessageType!);

            try
            {
                await cmd.ExecuteNonQueryAsync(_cancellation);
            }
            catch (OracleException e) when (e.Number == 1)
            {
                // Idempotent
            }

            // GH-4371, same reasoning as the connection-owning overload above, and the same place
            // MessageDatabase<T>.StoreOutgoingAsync(DbTransaction, Envelope[]) sets it
            envelope.WasPersistedInOutbox = true;
        }
    }
}
