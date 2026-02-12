using System.Data.Common;
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
        var cmd = conn.CreateCommand(
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
        var cmd = conn.CreateCommand(
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
            // Idempotent
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async Task DeleteOutgoingAsync(Envelope[] envelopes)
    {
        if (HasDisposed) return;
        if (envelopes.Length == 0) return;

        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);
        var cmd = conn.CreateCommand("");
        var placeholders = OracleCommandExtensions.WithEnvelopeIds(cmd, "id", envelopes);
        cmd.CommandText = $"DELETE FROM {SchemaName}.{DatabaseConstants.OutgoingTable} WHERE id IN ({placeholders})";
        await cmd.ExecuteNonQueryAsync(_cancellation);
        await conn.CloseAsync();
    }

    public async Task DeleteOutgoingAsync(Envelope envelope)
    {
        if (HasDisposed) return;

        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);
        var cmd = conn.CreateCommand(
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
                var deleteCmd = conn.CreateCommand("");
                var deletePlaceholders = OracleCommandExtensions.WithEnvelopeIds(deleteCmd, "id", discards);
                deleteCmd.CommandText =
                    $"DELETE FROM {SchemaName}.{DatabaseConstants.OutgoingTable} WHERE id IN ({deletePlaceholders})";
                await deleteCmd.ExecuteNonQueryAsync(_cancellation);
            }

            if (reassigned.Length > 0)
            {
                var reassignCmd = conn.CreateCommand("");
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

            var cmd = ((OracleConnection)tx.Connection!).CreateCommand(
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
        }
    }
}
