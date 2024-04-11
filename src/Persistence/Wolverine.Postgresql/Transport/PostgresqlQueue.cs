using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.Tables;
using Wolverine.Configuration;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Postgresql.Transport;

public class PostgresqlQueue : Endpoint, IBrokerQueue, IDatabaseBackedEndpoint
{
    private readonly string _queueTableName;
    private readonly string _scheduledTableName;
    private bool _hasInitialized;
    private readonly string _writeDirectlyToQueueTableSql;
    private readonly string _writeDirectlyToTheScheduledTable;
    private readonly string _moveFromOutgoingToQueueSql;
    private readonly string _moveFromOutgoingToScheduledSql;
    private readonly string _tryPopMessagesDirectlySql;

    public PostgresqlQueue(string name, PostgresqlTransport parent, EndpointRole role = EndpointRole.Application) :
        base(new Uri($"{PostgresqlTransport.ProtocolName}://{name}"), role)
    {
        Parent = parent;
        _queueTableName = $"wolverine_queue_{name}";
        _scheduledTableName = $"wolverine_queue_{name}_scheduled";

        Mode = EndpointMode.Durable;
        Name = name;
        EndpointName = name;

        QueueTable = new QueueTable(Parent, _queueTableName);
        ScheduledTable = new ScheduledMessageTable(Parent, _scheduledTableName);

        _tryPopMessagesDirectlySql = $@"
WITH message AS (
               DELETE 
               FROM {QueueTable.Identifier} WHERE CTID IN (SELECT ctid from {QueueTable.Identifier} ORDER BY {QueueTable.Identifier}.timestamp limit :COUNT FOR UPDATE SKIP LOCKED)        
               RETURNING {DatabaseConstants.Body}
)
SELECT message.{DatabaseConstants.Body} from message;
";

        _writeDirectlyToQueueTableSql =
            $@"insert into {QueueTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}) values (:id, :body, :type, :expires)";

        _writeDirectlyToTheScheduledTable = $@"
insert into {ScheduledTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}, {DatabaseConstants.ExecutionTime})
values (:id, :body, :type, :expires, :time)
ON CONFLICT ({DatabaseConstants.Id})
DO UPDATE SET {DatabaseConstants.Body} = :body, {DatabaseConstants.MessageType} = :type, {DatabaseConstants.KeepUntil} = :expires, {DatabaseConstants.ExecutionTime} = :time;
".Trim();

        _moveFromOutgoingToQueueSql = $@"
INSERT into {QueueTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}) 
SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.DeliverBy} 
FROM
    {Parent.SchemaName}.{DatabaseConstants.OutgoingTable} 
WHERE {DatabaseConstants.Id} = :id;
DELETE FROM {Parent.SchemaName}.{DatabaseConstants.OutgoingTable} WHERE {DatabaseConstants.Id} = :id;
";

        _moveFromOutgoingToScheduledSql = $@"
INSERT into {ScheduledTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.ExecutionTime}, {DatabaseConstants.KeepUntil}) 
SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, :time, {DatabaseConstants.DeliverBy} 
FROM
    {Parent.SchemaName}.{DatabaseConstants.OutgoingTable} 
WHERE {DatabaseConstants.Id} = :id;
DELETE FROM {Parent.SchemaName}.{DatabaseConstants.OutgoingTable} WHERE {DatabaseConstants.Id} = :id;
";

    }

    public string Name { get; }

    internal PostgresqlTransport Parent { get; }

    internal Table QueueTable { get; private set; }

    internal Table ScheduledTable { get; private set; }

    protected override bool supportsMode(EndpointMode mode)
    {
        return mode == EndpointMode.Durable || mode == EndpointMode.BufferedInMemory;
    }

    /// <summary>
    ///     The maximum number of messages to receive in a single batch when listening
    ///     in either buffered or durable modes. The default is 20.
    /// </summary>
    public int MaximumMessagesToReceive { get; set; } = 20;

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        var listener = new PostgresqlQueueListener(this, runtime, receiver);
        return ValueTask.FromResult<IListener>(listener);
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        return new PostgresqlQueueSender(this);
    }
    
    public override async ValueTask InitializeAsync(ILogger logger)
    {
        if (_hasInitialized)
        {
            return;
        }

        if (Parent.AutoProvision)
        {
            await SetupAsync(logger);
        }

        if (Parent.AutoPurgeAllQueues)
        {
            await PurgeAsync(logger);
        }

        _hasInitialized = true;
    }

    private NpgsqlDataSource dataSource => Parent.Store?.DataSource ?? throw new InvalidOperationException("The PostgreSQL transport has not been successfully initialized");

    public async ValueTask PurgeAsync(ILogger logger)
    {

        var builder = new BatchBuilder();
        builder.Append($"delete from {QueueTable.Identifier}");
        builder.StartNewCommand();
        builder.Append($"delete from {ScheduledTable.Identifier}");

        await using var batch = builder.Compile();
        await using var conn = await dataSource.OpenConnectionAsync();
        try
        {
            batch.Connection = conn;
            await batch.ExecuteNonQueryAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async ValueTask<Dictionary<string, string>> GetAttributesAsync()
    {
        var count = await CountAsync();
        var scheduled = await ScheduledCountAsync();

        return new Dictionary<string, string>
            { { "Name", Name }, { "Count", count.ToString() }, { "Scheduled", scheduled.ToString() } };
    }

    public async ValueTask<bool> CheckAsync()
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        try
        {
            var queueDelta = await QueueTable!.FindDeltaAsync(conn);
            if (queueDelta.HasChanges()) return false;
        
            var scheduledDelta = await ScheduledTable!.FindDeltaAsync(conn);
        
            return !scheduledDelta.HasChanges();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async ValueTask TeardownAsync(ILogger logger)
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        await QueueTable!.DropAsync(conn);
        await ScheduledTable!.DropAsync(conn);
        
        await conn.CloseAsync();
    }

    public async ValueTask SetupAsync(ILogger logger)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        
        await QueueTable!.ApplyChangesAsync(conn);
        await ScheduledTable!.ApplyChangesAsync(conn);
        
        await conn.CloseAsync();
    }

    public async Task SendAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
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

    public async Task ScheduleMessageAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
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
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
        await conn.CreateCommand($"delete from {Parent.SchemaName}.{DatabaseConstants.IncomingTable} where id = @id;" + _writeDirectlyToTheScheduledTable)
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

    public async Task MoveFromOutgoingToQueueAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);

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
        
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);

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
                        $"delete * from {Parent.SchemaName}.{DatabaseConstants.OutgoingTable} where id = @id")
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

    public async Task<long> MoveScheduledToReadyQueueAsync(CancellationToken cancellationToken)
    {
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);

        long count = 0;
        
        try
        {
            var builder = new BatchBuilder();
            builder.Append($"create temporary table temp_move_{Name} as select id, body, message_type, keep_until from {ScheduledTable.Identifier} WHERE {DatabaseConstants.ExecutionTime} <= (now() at time zone 'utc') AND ID NOT IN (select id from {QueueTable.Identifier}) for update skip locked");
            builder.StartNewCommand();
            builder.Append($"INSERT INTO {QueueTable.Identifier} (id, body, message_type, keep_until) SELECT id, body, message_type, keep_until FROM temp_move_{Name}");
            builder.StartNewCommand();
            builder.Append($"DELETE from {ScheduledTable.Identifier} where id in (select id from temp_move_{Name})");
            builder.StartNewCommand();
            builder.Append($"select count(*) from temp_move_{Name}");

            var batch = builder.Compile();
            batch.Connection = conn;
            
            count = (long)(await batch
                .ExecuteScalarAsync(cancellationToken));
        }
        finally
        {
            await conn.CloseAsync();
        }

        return count;
    }

    public async Task DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
        
        try
        {
            var builder = new BatchBuilder();
            builder.Append($"delete from {QueueTable.Identifier} where {DatabaseConstants.KeepUntil} IS NOT NULL and {DatabaseConstants.KeepUntil} <= (now() at time zone 'utc')");
            builder.StartNewCommand();
            builder.Append($"delete from {ScheduledTable.Identifier} where {DatabaseConstants.KeepUntil} IS NOT NULL and {DatabaseConstants.KeepUntil} <= (now() at time zone 'utc')");
            var batch = builder.Compile();

            batch.Connection = conn;

            await batch.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }


    public async Task<long> CountAsync()
    {
        await using var conn = await dataSource.OpenConnectionAsync();

        try
        {
            return (long)await conn.CreateCommand($"select count(*) from {QueueTable.Identifier}").ExecuteScalarAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async Task<long> ScheduledCountAsync()
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        try
        {
            return (long)await conn.CreateCommand($"select count(*) from {ScheduledTable.Identifier}").ExecuteScalarAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async Task<IReadOnlyList<Envelope>> TryPopAsync(int count, ILogger logger,
        CancellationToken cancellationToken)
    {
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);

        try
        {
            return await conn
                .CreateCommand(_tryPopMessagesDirectlySql)
                .With("count", count)
                .FetchListAsync<Envelope>(async reader =>
                {
                    var data = await reader.GetFieldValueAsync<byte[]>(0, cancellationToken);
                    try
                    {
                        return EnvelopeSerializer.Deserialize(data);
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "Error trying to deserialize Envelope data in Sql Transport Queue {Queue}, discarding", Name);
                        return Envelope.ForPing(Uri); // just a stand in
                    }
                }, cancellation: cancellationToken);
        }
        finally
        {
            await conn.CloseAsync();
        }

    }
    
    public async Task<IReadOnlyList<Envelope>> TryPopDurablyAsync(int count, DurabilitySettings settings,
        ILogger logger, CancellationToken cancellationToken)
    {
        var builder = new BatchBuilder();
        builder.Append($"delete FROM {QueueTable.Identifier} where id in (select id from {Parent.SchemaName}.{DatabaseConstants.IncomingTable})");
        builder.StartNewCommand();
        builder.Append($"create temporary table temp_pop_{Name} as select id, body, message_type, keep_until from {QueueTable.Identifier} ORDER BY {QueueTable.Identifier}.timestamp limit ");
        builder.AppendParameter(count);
        builder.Append(" for update skip locked");
        
        builder.StartNewCommand();
        builder.Append($"delete from {QueueTable.Identifier} where id in (select id from temp_pop_{Name})");
        builder.StartNewCommand();
        var parameters = builder.AppendWithParameters($"INSERT INTO {Parent.SchemaName}.{DatabaseConstants.IncomingTable} (id, status, owner_id, body, message_type, received_at, keep_until) SELECT id, 'Incoming', ?, body, message_type, '{Uri}', keep_until FROM temp_pop_{Name}");
        parameters[0].Value = settings.AssignedNodeNumber;
        parameters[0].NpgsqlDbType = NpgsqlDbType.Integer;
        
        builder.StartNewCommand();
        builder.Append($"select body from temp_pop_{Name}");
        var batch = builder.Compile();
        
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
        
        try
        {
            batch.Connection = conn;
            await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
            var list = new List<Envelope>();

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var data = await reader.GetFieldValueAsync<byte[]>(0, cancellationToken);
                try
                {
                    var e = EnvelopeSerializer.Deserialize(data);
                    list.Add(e);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error trying to deserialize Envelope data in Sql Transport Queue {Queue}, discarding", Name);
                    var ping = Envelope.ForPing(Uri); // just a stand in
                    list.Add(ping);
                }
            }

            await reader.CloseAsync();

            return list;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
    
}
    


