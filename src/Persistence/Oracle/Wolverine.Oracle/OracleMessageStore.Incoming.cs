using System.Data.Common;
using JasperFx.Core.Reflection;
using Oracle.ManagedDataAccess.Client;
using Weasel.Oracle;
using Wolverine.Oracle.Util;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.Oracle;

internal partial class OracleMessageStore
{
    public async Task StoreIncomingAsync(Envelope envelope)
    {
        var data = envelope.Status == EnvelopeStatus.Handled
            ? Array.Empty<byte>()
            : EnvelopeSerializer.Serialize(envelope);

        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);
        var cmd = conn.CreateCommand(
            $"INSERT INTO {SchemaName}.{DatabaseConstants.IncomingTable} ({DatabaseConstants.IncomingFields}) " +
            "VALUES (:body, :id, :status, :ownerId, :executionTime, :attempts, :messageType, :receivedAt, :keepUntil)");

        cmd.Parameters.Add(new OracleParameter("body", OracleDbType.Blob) { Value = data });
        cmd.With("id", envelope.Id);
        cmd.With("status", envelope.Status.ToString());
        cmd.With("ownerId", envelope.OwnerId);
        cmd.Parameters.Add(new OracleParameter("executionTime", OracleDbType.TimeStampTZ) { Value = (object?)envelope.ScheduledTime ?? DBNull.Value });
        cmd.With("attempts", envelope.Attempts);
        cmd.With("messageType", envelope.MessageType!);
        cmd.With("receivedAt", envelope.Destination?.ToString() ?? string.Empty);
        cmd.Parameters.Add(new OracleParameter("keepUntil", OracleDbType.TimeStampTZ) { Value = (object?)envelope.KeepUntil ?? DBNull.Value });

        try
        {
            await cmd.ExecuteNonQueryAsync(_cancellation);
        }
        catch (OracleException e) when (e.Number == 1) // ORA-00001: unique constraint violated
        {
            throw new DuplicateIncomingEnvelopeException(envelope);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async Task StoreIncomingAsync(IReadOnlyList<Envelope> envelopes)
    {
        if (envelopes.Count == 0) return;

        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);
        var tx = (OracleTransaction)await conn.BeginTransactionAsync(_cancellation);

        foreach (var envelope in envelopes)
        {
            var data = envelope.Status == EnvelopeStatus.Handled
                ? Array.Empty<byte>()
                : EnvelopeSerializer.Serialize(envelope);

            var cmd = conn.CreateCommand(
                $"INSERT INTO {SchemaName}.{DatabaseConstants.IncomingTable} ({DatabaseConstants.IncomingFields}) " +
                "VALUES (:body, :id, :status, :ownerId, :executionTime, :attempts, :messageType, :receivedAt, :keepUntil)");
            cmd.Transaction = tx;

            cmd.Parameters.Add(new OracleParameter("body", OracleDbType.Blob) { Value = data });
            cmd.With("id", envelope.Id);
            cmd.With("status", envelope.Status.ToString());
            cmd.With("ownerId", envelope.OwnerId);
            cmd.Parameters.Add(new OracleParameter("executionTime", OracleDbType.TimeStampTZ) { Value = (object?)envelope.ScheduledTime ?? DBNull.Value });
            cmd.With("attempts", envelope.Attempts);
            cmd.With("messageType", envelope.MessageType!);
            cmd.With("receivedAt", envelope.Destination?.ToString() ?? string.Empty);
            cmd.Parameters.Add(new OracleParameter("keepUntil", OracleDbType.TimeStampTZ) { Value = (object?)envelope.KeepUntil ?? DBNull.Value });

            try
            {
                await cmd.ExecuteNonQueryAsync(_cancellation);
            }
            catch (OracleException e) when (e.Number == 1)
            {
                // Idempotent
            }
        }

        await tx.CommitAsync(_cancellation);
        await conn.CloseAsync();
    }

    public async Task<bool> ExistsAsync(Envelope envelope, CancellationToken cancellation)
    {
        if (HasDisposed) return false;

        await using var conn = await _dataSource.OpenConnectionAsync(cancellation);
        OracleCommand cmd;

        if (_durability.MessageIdentity == MessageIdentity.IdOnly)
        {
            cmd = conn.CreateCommand(
                $"SELECT COUNT(id) FROM {SchemaName}.{DatabaseConstants.IncomingTable} WHERE id = :id");
            cmd.With("id", envelope.Id);
        }
        else
        {
            cmd = conn.CreateCommand(
                $"SELECT COUNT(id) FROM {SchemaName}.{DatabaseConstants.IncomingTable} WHERE id = :id AND {DatabaseConstants.ReceivedAt} = :destination");
            cmd.With("id", envelope.Id);
            cmd.With("destination", envelope.Destination!.ToString());
        }

        var count = await cmd.ExecuteScalarAsync(cancellation);
        await conn.CloseAsync();

        return Convert.ToInt64(count) > 0;
    }

    public async Task RescheduleExistingEnvelopeForRetryAsync(Envelope envelope)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);
        var cmd = conn.CreateCommand(
            $"UPDATE {SchemaName}.{DatabaseConstants.IncomingTable} SET " +
            $"{DatabaseConstants.ExecutionTime} = :time, {DatabaseConstants.Attempts} = :attempts " +
            "WHERE id = :id");
        cmd.With("id", envelope.Id);
        cmd.Parameters.Add(new OracleParameter("time", OracleDbType.TimeStampTZ) { Value = envelope.ScheduledTime });
        cmd.With("attempts", envelope.Attempts);
        await cmd.ExecuteNonQueryAsync(_cancellation);
        await conn.CloseAsync();
    }

    public async Task ScheduleExecutionAsync(Envelope envelope)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);
        var cmd = conn.CreateCommand(
            $"UPDATE {SchemaName}.{DatabaseConstants.IncomingTable} SET " +
            $"{DatabaseConstants.ExecutionTime} = :time, {DatabaseConstants.Status} = '{EnvelopeStatus.Scheduled}', " +
            $"{DatabaseConstants.Attempts} = :attempts, {DatabaseConstants.OwnerId} = {TransportConstants.AnyNode} " +
            $"WHERE id = :id AND {DatabaseConstants.ReceivedAt} = :uri");
        cmd.With("id", envelope.Id);
        cmd.Parameters.Add(new OracleParameter("time", OracleDbType.TimeStampTZ) { Value = envelope.ScheduledTime!.Value });
        cmd.With("attempts", envelope.Attempts);
        cmd.With("uri", envelope.Destination?.ToString() ?? string.Empty);
        await cmd.ExecuteNonQueryAsync(_cancellation);
        await conn.CloseAsync();
    }

    public async Task MoveToDeadLetterStorageAsync(Envelope envelope, Exception? exception)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);
        var tx = (OracleTransaction)await conn.BeginTransactionAsync(_cancellation);

        // Delete from incoming
        var deleteCmd = conn.CreateCommand(
            $"DELETE FROM {SchemaName}.{DatabaseConstants.IncomingTable} WHERE id = :id");
        deleteCmd.Transaction = tx;
        deleteCmd.With("id", envelope.Id);
        await deleteCmd.ExecuteNonQueryAsync(_cancellation);

        // Insert into dead letters
        byte[] data;
        try
        {
            data = EnvelopeSerializer.Serialize(envelope);
        }
        catch
        {
            data = Array.Empty<byte>();
        }

        var deadLetterFields = DatabaseConstants.DeadLetterFields;
        var deadLetterValues =
            ":id, :executionTime, :body, :messageType, :receivedAt, :source, :exceptionType, :exceptionMessage, :sentAt, :replayable";

        if (_durability.DeadLetterQueueExpirationEnabled)
        {
            deadLetterFields += ", " + DatabaseConstants.Expires;
            deadLetterValues += ", :expires";
        }

        var insertCmd = conn.CreateCommand(
            $"INSERT INTO {SchemaName}.{DatabaseConstants.DeadLetterTable} ({deadLetterFields}) " +
            $"VALUES ({deadLetterValues})");
        insertCmd.Transaction = tx;

        insertCmd.With("id", envelope.Id);
        insertCmd.Parameters.Add(new OracleParameter("executionTime", OracleDbType.TimeStampTZ) { Value = (object?)envelope.ScheduledTime ?? DBNull.Value });
        insertCmd.Parameters.Add(new OracleParameter("body", OracleDbType.Blob) { Value = data });
        insertCmd.With("messageType", envelope.MessageType ?? string.Empty);
        insertCmd.With("receivedAt", envelope.Destination?.ToString() ?? string.Empty);
        insertCmd.With("source", envelope.Source ?? string.Empty);
        insertCmd.With("exceptionType", exception?.GetType().FullNameInCode() ?? string.Empty);
        insertCmd.With("exceptionMessage", exception?.Message ?? string.Empty);
        insertCmd.Parameters.Add(new OracleParameter("sentAt", OracleDbType.TimeStampTZ) { Value = envelope.SentAt.ToUniversalTime() });
        insertCmd.With("replayable", 0); // Oracle stores bool as NUMBER(1)

        if (_durability.DeadLetterQueueExpirationEnabled)
        {
            var expiration = envelope.DeliverBy ?? DateTimeOffset.UtcNow.Add(_durability.DeadLetterQueueExpiration);
            insertCmd.Parameters.Add(new OracleParameter("expires", OracleDbType.TimeStampTZ) { Value = expiration });
        }

        await insertCmd.ExecuteNonQueryAsync(_cancellation);

        await tx.CommitAsync(_cancellation);
        await conn.CloseAsync();
    }

    public async Task IncrementIncomingEnvelopeAttemptsAsync(Envelope envelope)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);
        var cmd = conn.CreateCommand(
            $"UPDATE {SchemaName}.{DatabaseConstants.IncomingTable} SET {DatabaseConstants.Attempts} = :attempts " +
            $"WHERE id = :id AND {DatabaseConstants.ReceivedAt} = :uri");
        cmd.With("attempts", envelope.Attempts);
        cmd.With("id", envelope.Id);
        cmd.With("uri", envelope.Destination?.ToString() ?? string.Empty);
        await cmd.ExecuteNonQueryAsync(_cancellation);
        await conn.CloseAsync();
    }

    public async Task MarkIncomingEnvelopeAsHandledAsync(Envelope envelope)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);
        var cmd = conn.CreateCommand(
            $"UPDATE {SchemaName}.{DatabaseConstants.IncomingTable} SET " +
            $"{DatabaseConstants.Status} = '{EnvelopeStatus.Handled}', {DatabaseConstants.KeepUntil} = :keepUntil " +
            $"WHERE id = :id AND {DatabaseConstants.ReceivedAt} = :uri");
        cmd.Parameters.Add(new OracleParameter("keepUntil", OracleDbType.TimeStampTZ) { Value = (object?)envelope.KeepUntil ?? DBNull.Value });
        cmd.With("id", envelope.Id);
        cmd.With("uri", envelope.Destination?.ToString() ?? string.Empty);
        await cmd.ExecuteNonQueryAsync(_cancellation);
        await conn.CloseAsync();
    }

    public async Task MarkIncomingEnvelopeAsHandledAsync(IReadOnlyList<Envelope> envelopes)
    {
        if (envelopes.Count == 0) return;

        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);
        var tx = (OracleTransaction)await conn.BeginTransactionAsync(_cancellation);

        foreach (var envelope in envelopes)
        {
            var cmd = conn.CreateCommand(
                $"UPDATE {SchemaName}.{DatabaseConstants.IncomingTable} SET " +
                $"{DatabaseConstants.Status} = '{EnvelopeStatus.Handled}', {DatabaseConstants.KeepUntil} = :keepUntil " +
                $"WHERE id = :id AND {DatabaseConstants.ReceivedAt} = :uri");
            cmd.Transaction = tx;
            cmd.Parameters.Add(new OracleParameter("keepUntil", OracleDbType.TimeStampTZ) { Value = (object?)envelope.KeepUntil ?? DBNull.Value });
            cmd.With("id", envelope.Id);
            cmd.With("uri", envelope.Destination?.ToString() ?? string.Empty);
            await cmd.ExecuteNonQueryAsync(_cancellation);
        }

        await tx.CommitAsync(_cancellation);
        await conn.CloseAsync();
    }

    public async Task ReleaseIncomingAsync(int ownerId, Uri receivedAt)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);
        var cmd = conn.CreateCommand(
            $"UPDATE {SchemaName}.{DatabaseConstants.IncomingTable} SET " +
            $"{DatabaseConstants.OwnerId} = 0 " +
            $"WHERE {DatabaseConstants.OwnerId} = :ownerId AND {DatabaseConstants.ReceivedAt} = :uri");
        cmd.With("ownerId", ownerId);
        cmd.With("uri", receivedAt.ToString());
        await cmd.ExecuteNonQueryAsync(_cancellation);
        await conn.CloseAsync();
    }

    public async Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress, int limit)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);
        var cmd = conn.CreateCommand(
            $"SELECT {DatabaseConstants.IncomingFields} FROM {SchemaName}.{DatabaseConstants.IncomingTable} " +
            $"WHERE owner_id = {TransportConstants.AnyNode} AND status = '{EnvelopeStatus.Incoming}' AND {DatabaseConstants.ReceivedAt} = :address " +
            $"FETCH FIRST :limit ROWS ONLY");
        cmd.With("address", listenerAddress.ToString());
        cmd.With("limit", limit);

        var list = await cmd.FetchListAsync(r => OracleEnvelopeReader.ReadIncomingAsync(r), _cancellation);
        await conn.CloseAsync();
        return list;
    }

    public async Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming)
    {
        if (incoming.Count == 0) return;

        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);
        var tx = (OracleTransaction)await conn.BeginTransactionAsync(_cancellation);

        foreach (var envelope in incoming)
        {
            var cmd = conn.CreateCommand(
                $"UPDATE {SchemaName}.{DatabaseConstants.IncomingTable} SET {DatabaseConstants.OwnerId} = :owner " +
                "WHERE id = :id");
            cmd.Transaction = tx;
            cmd.With("owner", ownerId);
            cmd.With("id", envelope.Id);
            await cmd.ExecuteNonQueryAsync(_cancellation);
        }

        await tx.CommitAsync(_cancellation);
        await conn.CloseAsync();
    }

    // IMessageDatabase
    public async Task StoreIncomingAsync(DbTransaction tx, Envelope[] envelopes)
    {
        foreach (var envelope in envelopes)
        {
            var data = envelope.Status == EnvelopeStatus.Handled
                ? Array.Empty<byte>()
                : EnvelopeSerializer.Serialize(envelope);

            var cmd = ((OracleConnection)tx.Connection!).CreateCommand(
                $"INSERT INTO {SchemaName}.{DatabaseConstants.IncomingTable} ({DatabaseConstants.IncomingFields}) " +
                "VALUES (:body, :id, :status, :ownerId, :executionTime, :attempts, :messageType, :receivedAt, :keepUntil)");
            cmd.Transaction = (OracleTransaction)tx;

            cmd.Parameters.Add(new OracleParameter("body", OracleDbType.Blob) { Value = data });
            cmd.With("id", envelope.Id);
            cmd.With("status", envelope.Status.ToString());
            cmd.With("ownerId", envelope.OwnerId);
            cmd.Parameters.Add(new OracleParameter("executionTime", OracleDbType.TimeStampTZ) { Value = (object?)envelope.ScheduledTime ?? DBNull.Value });
            cmd.With("attempts", envelope.Attempts);
            cmd.With("messageType", envelope.MessageType!);
            cmd.With("receivedAt", envelope.Destination?.ToString() ?? string.Empty);
            cmd.Parameters.Add(new OracleParameter("keepUntil", OracleDbType.TimeStampTZ) { Value = (object?)envelope.KeepUntil ?? DBNull.Value });

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
