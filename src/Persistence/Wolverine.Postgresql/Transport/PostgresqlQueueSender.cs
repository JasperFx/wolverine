using JasperFx.Core;
using Npgsql;
using Oakton.Descriptions;
using Weasel.Core;
using Weasel.Postgresql;
using Wolverine.Configuration;
using Wolverine.RDBMS;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports.Sending;

namespace Wolverine.Postgresql.Transport;

internal interface IPostgresqlQueueSender : ISender
{
    Task ScheduleRetryAsync(Envelope envelope, CancellationToken cancellationToken);
}

internal class PostgresqlQueueSender : IPostgresqlQueueSender
{
    private readonly PostgresqlQueue _queue;
    private readonly NpgsqlDataSource _dataSource;

    private readonly string _moveFromOutgoingToQueueSql;
    private readonly string _moveFromOutgoingToScheduledSql;
    private readonly string _writeDirectlyToQueueTableSql;
    private readonly string _writeDirectlyToTheScheduledTable;
    private readonly string _schemaName;

    // Strictly for testing
    public PostgresqlQueueSender(PostgresqlQueue queue) : this(queue, queue.DataSource, null)
    {
        Destination = queue.Uri;
    }

    public PostgresqlQueueSender(PostgresqlQueue queue, NpgsqlDataSource dataSource, string? databaseName)
    {
        _queue = queue;
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

        Destination = PostgresqlQueue.ToUri(queue.Name, databaseName);

        _schemaName = queue.Parent.SchemaName;
        _moveFromOutgoingToQueueSql = $@"
INSERT into {queue.QueueTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}) 
SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.DeliverBy} 
FROM
    {_schemaName}.{DatabaseConstants.OutgoingTable} 
WHERE {DatabaseConstants.Id} = :id;
DELETE FROM {_schemaName}.{DatabaseConstants.OutgoingTable} WHERE {DatabaseConstants.Id} = :id;
";

        _moveFromOutgoingToScheduledSql = $@"
INSERT into {queue.ScheduledTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.ExecutionTime}, {DatabaseConstants.KeepUntil}) 
SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, :time, {DatabaseConstants.DeliverBy} 
FROM
    {_schemaName}.{DatabaseConstants.OutgoingTable} 
WHERE {DatabaseConstants.Id} = :id;
DELETE FROM {_schemaName}.{DatabaseConstants.OutgoingTable} WHERE {DatabaseConstants.Id} = :id;
";

        _writeDirectlyToQueueTableSql =
            $@"insert into {queue.QueueTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}) values (:id, :body, :type, :expires)";

        _writeDirectlyToTheScheduledTable = $@"
insert into {queue.ScheduledTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}, {DatabaseConstants.ExecutionTime})
values (:id, :body, :type, :expires, :time)
ON CONFLICT ({DatabaseConstants.Id})
DO UPDATE SET {DatabaseConstants.Body} = :body, {DatabaseConstants.MessageType} = :type, {DatabaseConstants.KeepUntil} = :expires, {DatabaseConstants.ExecutionTime} = :time;
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
        await conn.CreateCommand($"delete from {_schemaName}.{DatabaseConstants.IncomingTable} where id = :id;" + _writeDirectlyToTheScheduledTable)
            .With("id", envelope.Id)
            .With("body", EnvelopeSerializer.Serialize(envelope))
            .With("type", envelope.MessageType)
            .With("expires", envelope.DeliverBy)
            .With("time", envelope.ScheduledTime)
            .ExecuteNonQueryAsync(cancellationToken);


        try
        {
            var tx = conn.BeginTransactionAsync(cancellationToken);
            await scheduleMessageAsync(envelope, cancellationToken, conn);
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
        catch (NpgsqlException e)
        {
            // Making this idempotent, but optimistically
            if (e.Message.ContainsIgnoreCase("duplicate key value")) return;
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
        catch (NpgsqlException e)
        {
            if (e.Message.ContainsIgnoreCase("duplicate key value"))
            {
                await conn.CreateCommand(
                        $"delete * from {_schemaName}.{DatabaseConstants.OutgoingTable} where id = @id")
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
                catch (NpgsqlException e)
                {
                    // Making this idempotent, but optimistically
                    if (e.Message.ContainsIgnoreCase("duplicate key value")) return;
                    throw;
                }
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private async Task scheduleMessageAsync(Envelope envelope, CancellationToken cancellationToken, NpgsqlConnection conn)
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