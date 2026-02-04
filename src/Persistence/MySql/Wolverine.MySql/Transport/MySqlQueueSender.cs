using JasperFx.Core;
using MySqlConnector;
using Weasel.MySql;
using Wolverine.Configuration;
using Wolverine.RDBMS;
using Wolverine.Runtime.Serialization;

namespace Wolverine.MySql.Transport;

internal class MySqlQueueSender : IMySqlQueueSender
{
    private readonly MySqlQueue _queue;
    private readonly MySqlDataSource _dataSource;

    private readonly string _moveFromOutgoingToQueueSql;
    private readonly string _moveFromOutgoingToScheduledSql;
    private readonly string _writeDirectlyToQueueTableSql;
    private readonly string _writeDirectlyToTheScheduledTable;
    private readonly string _schemaName;

    // Strictly for testing
    public MySqlQueueSender(MySqlQueue queue) : this(queue, queue.DataSource, null)
    {
        Destination = queue.Uri;
    }

    public MySqlQueueSender(MySqlQueue queue, MySqlDataSource dataSource, string? databaseName)
    {
        _queue = queue;
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

        Destination = MySqlQueue.ToUri(queue.Name, databaseName);

        _schemaName = queue.Parent.TransportSchemaName;
        _moveFromOutgoingToQueueSql = $@"
INSERT INTO {queue.QueueTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil})
SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.DeliverBy}
FROM {queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable}
WHERE {DatabaseConstants.Id} = @id;
DELETE FROM {queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable} WHERE {DatabaseConstants.Id} = @id;
";

        _moveFromOutgoingToScheduledSql = $@"
INSERT INTO {queue.ScheduledTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.ExecutionTime}, {DatabaseConstants.KeepUntil})
SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, @time, {DatabaseConstants.DeliverBy}
FROM {queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable}
WHERE {DatabaseConstants.Id} = @id;
DELETE FROM {queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable} WHERE {DatabaseConstants.Id} = @id;
";

        _writeDirectlyToQueueTableSql =
            $@"INSERT INTO {queue.QueueTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}) VALUES (@id, @body, @type, @expires)";

        _writeDirectlyToTheScheduledTable = $@"
INSERT INTO {queue.ScheduledTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}, {DatabaseConstants.ExecutionTime})
VALUES (@id, @body, @type, @expires, @time)
ON DUPLICATE KEY UPDATE {DatabaseConstants.Body} = @body, {DatabaseConstants.MessageType} = @type, {DatabaseConstants.KeepUntil} = @expires, {DatabaseConstants.ExecutionTime} = @time
".Trim();
    }

    public bool SupportsNativeScheduledSend => true;
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

    public async Task ScheduleMessageAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await scheduleMessageAsync(envelope, cancellationToken, conn);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async Task ScheduleRetryAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await conn.CreateCommand($"DELETE FROM {_queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.IncomingTable} WHERE id = @id;" + _writeDirectlyToTheScheduledTable)
                .With("id", envelope.Id)
                .With("body", EnvelopeSerializer.Serialize(envelope))
                .With("type", envelope.MessageType)
                .With("expires", envelope.DeliverBy)
                .With("time", envelope.ScheduledTime)
                .ExecuteNonQueryAsync(cancellationToken);
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
            var count = await conn.CreateCommand(_moveFromOutgoingToQueueSql)
                .With("id", envelope.Id)
                .ExecuteNonQueryAsync(cancellationToken);

            if (count == 0) throw new InvalidOperationException("No matching outgoing envelope");
        }
        catch (MySqlException e)
        {
            // Making this idempotent, but optimistically
            if (e.Message.ContainsIgnoreCase("Duplicate entry")) return;
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

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        try
        {
            var count = await conn.CreateCommand(_moveFromOutgoingToScheduledSql)
                .With("id", envelope.Id)
                .With("time", envelope.ScheduledTime!.Value)
                .ExecuteNonQueryAsync(cancellationToken);

            if (count == 0) throw new InvalidOperationException($"No matching outgoing envelope for {envelope}");
        }
        catch (MySqlException e)
        {
            if (e.Message.ContainsIgnoreCase("Duplicate entry"))
            {
                await conn.CreateCommand(
                        $"DELETE FROM {_queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable} WHERE id = @id")
                    .With("id", envelope.Id)
                    .ExecuteNonQueryAsync(cancellationToken);

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
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
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
                    await conn.CreateCommand(_writeDirectlyToQueueTableSql)
                        .With("id", envelope.Id)
                        .With("body", EnvelopeSerializer.Serialize(envelope))
                        .With("type", envelope.MessageType)
                        .With("expires", envelope.DeliverBy)
                        .ExecuteNonQueryAsync(cancellationToken);
                }
                catch (MySqlException e)
                {
                    // Making this idempotent, but optimistically
                    if (e.Message.ContainsIgnoreCase("Duplicate entry")) return;
                    throw;
                }
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private async Task scheduleMessageAsync(Envelope envelope, CancellationToken cancellationToken, MySqlConnection conn)
    {
        await conn.CreateCommand(_writeDirectlyToTheScheduledTable)
            .With("id", envelope.Id)
            .With("body", EnvelopeSerializer.Serialize(envelope))
            .With("type", envelope.MessageType)
            .With("expires", envelope.DeliverBy)
            .With("time", envelope.ScheduledTime)
            .ExecuteNonQueryAsync(cancellationToken);
    }
}
