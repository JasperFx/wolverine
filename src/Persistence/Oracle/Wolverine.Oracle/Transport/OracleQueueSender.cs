using JasperFx.Core;
using Oracle.ManagedDataAccess.Client;
using Weasel.Oracle;
using Wolverine.Configuration;
using Wolverine.Oracle.Util;
using Wolverine.RDBMS;
using Wolverine.Runtime.Serialization;

namespace Wolverine.Oracle.Transport;

internal class OracleQueueSender : IOracleQueueSender
{
    private readonly OracleQueue _queue;
    private readonly OracleDataSource _dataSource;

    private readonly string _writeDirectlyToQueueTableSql;
    private readonly string _writeDirectlyToTheScheduledTable;
    private readonly string _schemaName;

    // Strictly for testing
    public OracleQueueSender(OracleQueue queue) : this(queue, queue.DataSource, null)
    {
        Destination = queue.Uri;
    }

    public OracleQueueSender(OracleQueue queue, OracleDataSource dataSource, string? databaseName)
    {
        _queue = queue;
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

        Destination = OracleQueue.ToUri(queue.Name, databaseName);

        _schemaName = queue.Parent.TransportSchemaName;

        _writeDirectlyToQueueTableSql =
            $"INSERT INTO {queue.QueueTable.Identifier.QualifiedName} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}) VALUES (:id, :body, :type, :expires)";

        _writeDirectlyToTheScheduledTable =
            $"MERGE INTO {queue.ScheduledTable.Identifier.QualifiedName} t USING DUAL ON (t.{DatabaseConstants.Id} = :id) " +
            $"WHEN MATCHED THEN UPDATE SET t.{DatabaseConstants.Body} = :body, t.{DatabaseConstants.MessageType} = :type, t.{DatabaseConstants.KeepUntil} = :expires, t.{DatabaseConstants.ExecutionTime} = :time " +
            $"WHEN NOT MATCHED THEN INSERT ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}, {DatabaseConstants.ExecutionTime}) VALUES (:id, :body, :type, :expires, :time)";
    }

    public bool SupportsNativeScheduledSend => true;
    public bool SupportsNativeScheduledCancellation => true;
    public Uri Destination { get; private set; }

    public async Task<bool> PingAsync()
    {
        try
        {
            await _queue.CheckAsync();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task ScheduleRetryAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            // Delete from incoming, write to scheduled
            var deleteCmd = conn.CreateCommand(
                $"DELETE FROM {_queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.IncomingTable} WHERE id = :id");
            deleteCmd.With("id", envelope.Id);
            await deleteCmd.ExecuteNonQueryAsync(cancellationToken);

            await writeToScheduledTableAsync(envelope, cancellationToken, conn);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async ValueTask SendAsync(Envelope envelope)
    {
        if (_queue.Mode == EndpointMode.Durable && envelope.WasPersistedInOutbox)
        {
            if (envelope.IsScheduledForLater(DateTimeOffset.UtcNow))
            {
                await MoveFromOutgoingToScheduledAsync(envelope, CancellationToken.None);
            }
            else
            {
                await MoveFromOutgoingToQueueAsync(envelope, CancellationToken.None);
            }
        }
        else
        {
            await SendAsync(envelope, CancellationToken.None);
        }
    }

    public async Task MoveFromOutgoingToQueueAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        try
        {
            var tx = (OracleTransaction)await conn.BeginTransactionAsync(cancellationToken);

            // Read from outgoing
            var readCmd = conn.CreateCommand(
                $"SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.DeliverBy} FROM {_queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable} WHERE {DatabaseConstants.Id} = :id FOR UPDATE");
            readCmd.Transaction = tx;
            readCmd.With("id", envelope.Id);

            await using var reader = await readCmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var id = await OracleEnvelopeReader.ReadGuidAsync(reader, 0, cancellationToken);
                var body = (byte[])reader.GetValue(1);
                var messageType = await reader.GetFieldValueAsync<string>(2, cancellationToken);
                DateTimeOffset? keepUntil = await reader.IsDBNullAsync(3, cancellationToken) ? null : await reader.GetFieldValueAsync<DateTimeOffset>(3, cancellationToken);

                await reader.CloseAsync();

                // Insert into queue
                var insertCmd = conn.CreateCommand(_writeDirectlyToQueueTableSql);
                insertCmd.Transaction = tx;
                insertCmd.With("id", id);
                insertCmd.Parameters.Add(new OracleParameter("body", OracleDbType.Blob) { Value = body });
                insertCmd.With("type", messageType);
                insertCmd.Parameters.Add(new OracleParameter("expires", OracleDbType.TimeStampTZ) { Value = (object?)keepUntil ?? DBNull.Value });
                await insertCmd.ExecuteNonQueryAsync(cancellationToken);

                // Delete from outgoing
                var deleteCmd = conn.CreateCommand(
                    $"DELETE FROM {_queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable} WHERE {DatabaseConstants.Id} = :id");
                deleteCmd.Transaction = tx;
                deleteCmd.With("id", id);
                await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                await reader.CloseAsync();
            }

            await tx.CommitAsync(cancellationToken);
        }
        catch (OracleException e) when (e.Number == 1) // ORA-00001: unique constraint violated
        {
            // Making this idempotent
            return;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async Task MoveFromOutgoingToScheduledAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (!envelope.ScheduledTime.HasValue)
            throw new InvalidOperationException("This envelope has no scheduled time");

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        try
        {
            var tx = (OracleTransaction)await conn.BeginTransactionAsync(cancellationToken);

            // Read from outgoing
            var readCmd = conn.CreateCommand(
                $"SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.DeliverBy} FROM {_queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable} WHERE {DatabaseConstants.Id} = :id FOR UPDATE");
            readCmd.Transaction = tx;
            readCmd.With("id", envelope.Id);

            await using var reader = await readCmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var id = await OracleEnvelopeReader.ReadGuidAsync(reader, 0, cancellationToken);
                var body = (byte[])reader.GetValue(1);
                var messageType = await reader.GetFieldValueAsync<string>(2, cancellationToken);
                DateTimeOffset? keepUntil = await reader.IsDBNullAsync(3, cancellationToken) ? null : await reader.GetFieldValueAsync<DateTimeOffset>(3, cancellationToken);

                await reader.CloseAsync();

                // Insert into scheduled
                var insertCmd = conn.CreateCommand(_writeDirectlyToTheScheduledTable);
                insertCmd.Transaction = tx;
                insertCmd.With("id", id);
                insertCmd.Parameters.Add(new OracleParameter("body", OracleDbType.Blob) { Value = body });
                insertCmd.With("type", messageType);
                insertCmd.Parameters.Add(new OracleParameter("expires", OracleDbType.TimeStampTZ) { Value = (object?)keepUntil ?? DBNull.Value });
                insertCmd.Parameters.Add(new OracleParameter("time", OracleDbType.TimeStampTZ) { Value = envelope.ScheduledTime!.Value });
                await insertCmd.ExecuteNonQueryAsync(cancellationToken);

                // Delete from outgoing
                var deleteCmd = conn.CreateCommand(
                    $"DELETE FROM {_queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable} WHERE {DatabaseConstants.Id} = :id");
                deleteCmd.Transaction = tx;
                deleteCmd.With("id", id);
                await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                await reader.CloseAsync();
            }

            await tx.CommitAsync(cancellationToken);
        }
        catch (OracleException e) when (e.Number == 1) // ORA-00001: unique constraint violated
        {
            // Clean up outgoing on duplicate
            await using var cleanConn = await _dataSource.OpenConnectionAsync(cancellationToken);
            try
            {
                var cleanCmd = cleanConn.CreateCommand(
                    $"DELETE FROM {_queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable} WHERE id = :id");
                cleanCmd.With("id", envelope.Id);
                await cleanCmd.ExecuteNonQueryAsync(cancellationToken);
            }
            finally
            {
                await cleanConn.CloseAsync();
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async Task SendAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            if (envelope.IsScheduledForLater(DateTimeOffset.UtcNow))
            {
                await writeToScheduledTableAsync(envelope, cancellationToken, conn);
            }
            else
            {
                try
                {
                    var cmd = conn.CreateCommand(_writeDirectlyToQueueTableSql);
                    cmd.With("id", envelope.Id);
                    cmd.Parameters.Add(new OracleParameter("body", OracleDbType.Blob) { Value = EnvelopeSerializer.Serialize(envelope) });
                    cmd.With("type", envelope.MessageType);
                    cmd.Parameters.Add(new OracleParameter("expires", OracleDbType.TimeStampTZ) { Value = (object?)envelope.DeliverBy ?? DBNull.Value });
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (OracleException e) when (e.Number == 1)
                {
                    // Making this idempotent
                    return;
                }
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private async Task writeToScheduledTableAsync(Envelope envelope, CancellationToken cancellationToken, OracleConnection conn)
    {
        var cmd = conn.CreateCommand(_writeDirectlyToTheScheduledTable);
        cmd.With("id", envelope.Id);
        cmd.Parameters.Add(new OracleParameter("body", OracleDbType.Blob) { Value = EnvelopeSerializer.Serialize(envelope) });
        cmd.With("type", envelope.MessageType);
        cmd.Parameters.Add(new OracleParameter("expires", OracleDbType.TimeStampTZ) { Value = (object?)envelope.DeliverBy ?? DBNull.Value });
        cmd.Parameters.Add(new OracleParameter("time", OracleDbType.TimeStampTZ) { Value = (object?)envelope.ScheduledTime ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        envelope.SchedulingToken = envelope.Id;
    }

    public async Task CancelScheduledMessageAsync(object schedulingToken, CancellationToken cancellation = default)
    {
        if (schedulingToken is not Guid envelopeId)
        {
            throw new ArgumentException(
                $"Expected scheduling token of type Guid for Oracle queue sender, got {schedulingToken?.GetType().Name ?? "null"}",
                nameof(schedulingToken));
        }

        await using var conn = await _dataSource.OpenConnectionAsync(cancellation);
        try
        {
            await conn.CreateCommand($"DELETE FROM {_queue.ScheduledTable.Identifier} WHERE id = :id")
                .With("id", envelopeId)
                .ExecuteNonQueryAsync(cancellation);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
