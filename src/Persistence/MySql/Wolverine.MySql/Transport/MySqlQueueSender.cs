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
    private readonly string _moveOrWriteToQueueTableSql;
    private readonly string _moveOrWriteToScheduledTableSql;
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

        // GH-3859: on a multi-tenanted host the queue tables live in each tenant's OWN database, so
        // every one of the statements below has to be built against THIS sender's data source rather
        // than the transport-wide schema name.
        var queueTable = queue.QueueTableFor(dataSource).Identifier;
        var scheduledTable = queue.ScheduledTableFor(dataSource).Identifier;

        _schemaName = queue.SchemaFor(dataSource);
        _moveFromOutgoingToQueueSql = $@"
INSERT INTO {queueTable} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil})
SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.DeliverBy}
FROM {queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable}
WHERE {DatabaseConstants.Id} = @id;
DELETE FROM {queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable} WHERE {DatabaseConstants.Id} = @id;
";

        _moveFromOutgoingToScheduledSql = $@"
INSERT INTO {scheduledTable} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.ExecutionTime}, {DatabaseConstants.KeepUntil})
SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, @time, {DatabaseConstants.DeliverBy}
FROM {queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable}
WHERE {DatabaseConstants.Id} = @id;
DELETE FROM {queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable} WHERE {DatabaseConstants.Id} = @id;
";

        _writeDirectlyToQueueTableSql =
            $@"INSERT INTO {queueTable} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}) VALUES (@id, @body, @type, @expires)";

        _writeDirectlyToTheScheduledTable = $@"
INSERT INTO {scheduledTable} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}, {DatabaseConstants.ExecutionTime})
VALUES (@id, @body, @type, @expires, @time)
ON DUPLICATE KEY UPDATE {DatabaseConstants.Body} = @body, {DatabaseConstants.MessageType} = @type, {DatabaseConstants.KeepUntil} = @expires, {DatabaseConstants.ExecutionTime} = @time
".Trim();

        // GH-5107. The envelope's outbox row may exist even though WasPersistedInOutbox is false: the EF Core
        // outbox writes it through its own unit of work, and outbox recovery reads it back without the flag.
        // Take the row out of the outbox in the same transaction that puts the envelope on the queue, so a
        // node dying in between can't leave a row behind for recovery to send again. With no outbox row,
        // this is the plain insert. A duplicate send is a no-op update rather than an error, so the
        // transaction still commits the delete.
        var deleteFromOutgoing =
            $"DELETE FROM {queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.OutgoingTable} WHERE {DatabaseConstants.Id} = @id;";

        _moveOrWriteToQueueTableSql =
            $"{_writeDirectlyToQueueTableSql} ON DUPLICATE KEY UPDATE {DatabaseConstants.Id} = {DatabaseConstants.Id};\n{deleteFromOutgoing}";

        _moveOrWriteToScheduledTableSql = $"{_writeDirectlyToTheScheduledTable};\n{deleteFromOutgoing}";
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
        else if (_queue.Mode == EndpointMode.Durable)
        {
            await MoveOrWriteToQueueAsync(envelope, CancellationToken.None);
        }
        else
        {
            await SendAsync(envelope, CancellationToken.None);
        }
    }

    /// <summary>
    ///     GH-5107. Puts an envelope on the queue (or the scheduled table) and deletes its outbox row, if it has
    ///     one, in a single transaction
    /// </summary>
    public async Task MoveOrWriteToQueueAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        try
        {
            await using var tx = await conn.BeginTransactionAsync(cancellationToken);

            var scheduled = envelope.IsScheduledForLater(DateTimeOffset.UtcNow);
            var cmd = conn.CreateCommand(scheduled ? _moveOrWriteToScheduledTableSql : _moveOrWriteToQueueTableSql)
                .With("id", envelope.Id)
                .With("body", EnvelopeSerializer.Serialize(envelope))
                .With("type", envelope.MessageType)
                .With("expires", envelope.DeliverBy);

            if (scheduled)
            {
                cmd.With("time", envelope.ScheduledTime);
            }

            cmd.Transaction = tx;
            await cmd.ExecuteNonQueryAsync(cancellationToken);

            await tx.CommitAsync(cancellationToken);
        }
        finally
        {
            await conn.CloseAsync();
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
