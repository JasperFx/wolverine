using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Weasel.SqlServer;
using Wolverine.Configuration;
using Wolverine.RDBMS;
using Wolverine.Runtime.Serialization;

namespace Wolverine.SqlServer.Transport;

internal class SqlServerQueueSender : ISqlServerQueueSender
{
    private readonly SqlServerQueue _queue;
    private readonly string _connectionString;

    private readonly string _moveFromOutgoingToQueueSql;
    private readonly string _moveFromOutgoingToScheduledSql;
    private readonly string _writeDirectlyToQueueTableSql;
    private readonly string _writeDirectlyToTheScheduledTable;
    private readonly string _deleteFromIncomingAndScheduleSql;

    // Strictly for testing
    public SqlServerQueueSender(SqlServerQueue queue) : this(queue, queue.Parent.Settings.ConnectionString!, null)
    {
        Destination = queue.Uri;
    }

    public SqlServerQueueSender(SqlServerQueue queue, string connectionString, string? databaseName)
    {
        _queue = queue;
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));

        Destination = SqlServerQueue.ToUri(queue.Name, databaseName);

        _moveFromOutgoingToQueueSql = $@"
INSERT into {queue.QueueTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil})
SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.DeliverBy}
FROM
    {queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable}
WHERE {DatabaseConstants.Id} = @id;
DELETE FROM {queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable} WHERE {DatabaseConstants.Id} = @id;
";

        _moveFromOutgoingToScheduledSql = $@"
INSERT into {queue.ScheduledTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.ExecutionTime}, {DatabaseConstants.KeepUntil})
SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, @time, {DatabaseConstants.DeliverBy}
FROM
    {queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable}
WHERE {DatabaseConstants.Id} = @id;
DELETE FROM {queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable} WHERE {DatabaseConstants.Id} = @id;
";

        _writeDirectlyToQueueTableSql =
            $@"insert into {queue.QueueTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}) values (@id, @body, @type, @expires)";

        _writeDirectlyToTheScheduledTable = $@"
merge {queue.ScheduledTable.Identifier} as target
using (values (@id, @body, @type, @expires, @time)) as source ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}, {DatabaseConstants.ExecutionTime})
on target.id = @id
WHEN MATCHED THEN UPDATE set target.body = @body, target.{DatabaseConstants.ExecutionTime} = @time
WHEN NOT MATCHED THEN INSERT  ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}, {DatabaseConstants.ExecutionTime}) values (source.{DatabaseConstants.Id}, source.{DatabaseConstants.Body}, source.{DatabaseConstants.MessageType}, source.{DatabaseConstants.KeepUntil}, source.{DatabaseConstants.ExecutionTime});
";

        _deleteFromIncomingAndScheduleSql =
            $"delete from {queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.IncomingTable} where id = @id;" +
            _writeDirectlyToTheScheduledTable;
    }

    public bool SupportsNativeScheduledSend => true;
    public Uri Destination { get; }

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
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        try
        {
            await using var cmd = conn.CreateCommand(_deleteFromIncomingAndScheduleSql)
                .With("id", envelope.Id)
                .With("body", EnvelopeSerializer.Serialize(envelope))
                .With("type", envelope.MessageType!)
                .With("expires", envelope.DeliverBy!)
                .With("time", envelope.ScheduledTime!);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
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
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        try
        {
            await using var cmd = conn.CreateCommand(_moveFromOutgoingToQueueSql)
                .With("id", envelope.Id);
            var count = await cmd.ExecuteNonQueryAsync(cancellationToken);

            if (count == 0) throw new InvalidOperationException("No matching outgoing envelope");
        }
        catch (SqlException e)
        {
            // Idempotent on a duplicate send: 2627 = PK violation, 2601 = unique index violation.
            // Match on the error number rather than the message text, which is localized.
            if (e.Number is 2627 or 2601) return;
            throw;
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

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        try
        {
            await using var cmd = conn.CreateCommand(_moveFromOutgoingToScheduledSql)
                .With("id", envelope.Id)
                .With("time", envelope.ScheduledTime!.Value);
            var count = await cmd.ExecuteNonQueryAsync(cancellationToken);

            if (count == 0) throw new InvalidOperationException($"No matching outgoing envelope for {envelope}");
        }
        catch (SqlException e)
        {
            if (e.Number is 2627 or 2601)
            {
                await using var cleanupCmd = conn.CreateCommand(
                        $"delete from {_queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable} where id = @id")
                    .With("id", envelope.Id);
                await cleanupCmd.ExecuteNonQueryAsync(cancellationToken);

                return;
            }

            throw;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async Task SendAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        try
        {
            if (envelope.IsScheduledForLater(DateTimeOffset.UtcNow))
            {
                await scheduleMessageAsync(envelope, cancellationToken, conn);
            }
            else
            {
                try
                {
                    await using var cmd = conn.CreateCommand(_writeDirectlyToQueueTableSql)
                        .With("id", envelope.Id)
                        .With("body", EnvelopeSerializer.Serialize(envelope))
                        .With("type", envelope.MessageType!)
                        .With("expires", envelope.DeliverBy!);
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (SqlException e)
                {
                    // Making this idempotent, but optimistically
                    if (e.Number is 2627 or 2601) return;
                    throw;
                }
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private async Task scheduleMessageAsync(Envelope envelope, CancellationToken cancellationToken,
        SqlConnection conn)
    {
        await using var cmd = conn.CreateCommand(_writeDirectlyToTheScheduledTable)
            .With("id", envelope.Id)
            .With("body", EnvelopeSerializer.Serialize(envelope))
            .With("type", envelope.MessageType!)
            .With("expires", envelope.DeliverBy!)
            .With("time", envelope.ScheduledTime!);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
