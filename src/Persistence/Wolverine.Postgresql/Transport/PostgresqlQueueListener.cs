using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Weasel.Core;
using Weasel.Postgresql;
using Wolverine.Configuration;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.Postgresql.Transport;

internal class PostgresqlQueueListener : IListener
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly PostgresqlQueue _queue;
    private readonly IReceiver _receiver;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresqlQueueListener> _logger;
    private Task? _task;
    private readonly DurabilitySettings _settings;
    private Task? _scheduledTask;
    private readonly PostgresqlQueueSender _sender;
    private readonly string _tryPopMessagesDirectlySql;
    private readonly string _queueTableName;
    private readonly string _queueName;
    private readonly string _schemaName;
    private readonly string _scheduledTableName;

    public PostgresqlQueueListener(PostgresqlQueue queue, IWolverineRuntime runtime, IReceiver receiver,
        NpgsqlDataSource dataSource, string? databaseName)
    {
        Address = PostgresqlQueue.ToUri(queue.Name, databaseName);
        _queue = queue;
        _receiver = receiver;
        _dataSource = dataSource;
        _logger = runtime.LoggerFactory.CreateLogger<PostgresqlQueueListener>();
        _settings = runtime.DurabilitySettings;

        _sender = new PostgresqlQueueSender(queue, _dataSource, databaseName);

        _queueTableName = _queue.QueueTable.Identifier.QualifiedName;
        _scheduledTableName = _queue.ScheduledTable.Identifier.QualifiedName;
        _schemaName = _queue.Parent.MessageStorageSchemaName;

        _tryPopMessagesDirectlySql = $@"
WITH message AS (
               DELETE 
               FROM {_queueTableName} WHERE CTID IN (SELECT ctid from {_queueTableName} ORDER BY {_queueTableName}.timestamp limit :COUNT FOR UPDATE SKIP LOCKED)        
               RETURNING {DatabaseConstants.Body}
)
SELECT message.{DatabaseConstants.Body} from message;
";
        _queueName = _queue.Name;
    }

    public ValueTask CompleteAsync(Envelope envelope)
    {
        return ValueTask.CompletedTask;
    }

    public async ValueTask DeferAsync(Envelope envelope)
    {
        await _sender.SendAsync(envelope, _cancellation.Token);
    }

    public ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        _task.SafeDispose();
        _scheduledTask.SafeDispose();
        return ValueTask.CompletedTask;
    }

    public Uri Address { get; }
    public ValueTask StopAsync()
    {
        _cancellation.Cancel();

        _task?.SafeDispose();
        _scheduledTask?.SafeDispose();

        return ValueTask.CompletedTask;
    }

    private async Task lookForScheduledMessagesAsync()
    {
        // Little bit of randomness to keep each node from hammering the
        // table at the exact same time
        await Task.Delay(_settings.ScheduledJobFirstExecution);

        var failedCount = 0;

        while (!_cancellation.Token.IsCancellationRequested)
        {
            try
            {
                var count = await MoveScheduledToReadyQueueAsync(_cancellation.Token);
                if (count > 0)
                {
                    _logger.LogInformation("Propagated {Number} scheduled messages to PostgreSQL-backed queue {Queue}", count, _queueName);
                }

                await DeleteExpiredAsync(CancellationToken.None);

                failedCount = 0;

                await Task.Delay(_settings.ScheduledJobPollingTime);

            }
            catch (Exception e)
            {
                if (e is TaskCanceledException && _cancellation.IsCancellationRequested)
                {
                    break;
                }

                failedCount++;
                var pauseTime = failedCount > 5 ? 1.Seconds() : (failedCount * 100).Milliseconds();

                _logger.LogError(e, "Error while trying to propagate scheduled messages from PostgreSQL Queue {Name}",
                    _queueName);

                await Task.Delay(pauseTime);
            }
        }
    }

    public async Task<long> MoveScheduledToReadyQueueAsync(CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        long count = 0;

        try
        {
            var builder = new BatchBuilder();
            builder.Append($"create temporary table temp_move_{_queueName} as select id, body, message_type, keep_until from {_scheduledTableName} WHERE {DatabaseConstants.ExecutionTime} <= (now() at time zone 'utc') AND ID NOT IN (select id from {_queueTableName}) for update skip locked");
            builder.StartNewCommand();
            builder.Append($"INSERT INTO {_queueTableName} (id, body, message_type, keep_until) SELECT id, body, message_type, keep_until FROM temp_move_{_queueName}");
            builder.StartNewCommand();
            builder.Append($"DELETE from {_scheduledTableName} where id in (select id from temp_move_{_queueName})");
            builder.StartNewCommand();
            builder.Append($"select count(*) from temp_move_{_queueName}");

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

    private async Task listenForMessagesAsync()
    {
        var failedCount = 0;

        while (!_cancellation.Token.IsCancellationRequested)
        {
            try
            {

                var messages = _queue.Mode == EndpointMode.Durable
                    ? await TryPopDurablyAsync(_queue.MaximumMessagesToReceive, _settings, _logger,
                        _cancellation.Token)
                    : await TryPopAsync(_queue.MaximumMessagesToReceive, _logger, _cancellation.Token);

                failedCount = 0;

                if (messages.Any())
                {
                    await _receiver.ReceivedAsync(this, messages.ToArray());
                }
                else
                {
                    // Slow down if this is a periodically used queue
                    await Task.Delay(250.Milliseconds());
                }
            }
            catch (Exception e)
            {
                if (e is TaskCanceledException && _cancellation.IsCancellationRequested)
                {
                    break;
                }

                failedCount++;
                var pauseTime = failedCount > 5 ? 1.Seconds() : (failedCount * 100).Milliseconds();

                _logger.LogError(e, "Error while trying to retrieve messages from PostgreSQL Queue {Name}",
                    _queueName);

                await Task.Delay(pauseTime);
            }
        }
    }

    public async Task<IReadOnlyList<Envelope>> TryPopDurablyAsync(int count, DurabilitySettings settings,
        ILogger logger, CancellationToken cancellationToken)
    {
        var builder = new BatchBuilder();

        builder.Append($"delete FROM {_queueTableName} where id in (select id from {_schemaName}.{DatabaseConstants.IncomingTable})");
        builder.StartNewCommand();
        builder.Append($"create temporary table temp_pop_{_queueName} as select id, body, message_type, keep_until from {_queueTableName} ORDER BY {_queueTableName}.timestamp limit ");
        builder.AppendParameter(count);
        builder.Append(" for update skip locked");

        builder.StartNewCommand();
        builder.Append($"delete from {_queueTableName} where id in (select id from temp_pop_{_queueName})");
        builder.StartNewCommand();
        var parameters = builder.AppendWithParameters($"INSERT INTO {_schemaName}.{DatabaseConstants.IncomingTable} (id, status, owner_id, body, message_type, received_at, keep_until) SELECT id, 'Incoming', ?, body, message_type, '{Address}', keep_until FROM temp_pop_{_queueName}");
        parameters[0].Value = settings.AssignedNodeNumber;
        parameters[0].NpgsqlDbType = NpgsqlDbType.Integer;

        builder.StartNewCommand();
        builder.Append($"select body from temp_pop_{_queueName}");
        var batch = builder.Compile();

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

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
                    logger.LogError(e, "Error trying to deserialize Envelope data in Sql Transport Queue {Queue}, discarding", _queueName);
                    var ping = Envelope.ForPing(Address); // just a stand in
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

    public async Task<IReadOnlyList<Envelope>> TryPopAsync(int count, ILogger logger,
        CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        try
        {
            return await conn.CreateCommand(_tryPopMessagesDirectlySql)
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
                        logger.LogError(e, "Error trying to deserialize Envelope data in Sql Transport Queue {Queue}, discarding", _queueName);
                        return Envelope.ForPing(Address); // just a stand in
                    }
                }, cancellation: cancellationToken);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async Task DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        try
        {
            var builder = new BatchBuilder();
            builder.Append($"delete from {_queueTableName} where {DatabaseConstants.KeepUntil} IS NOT NULL and {DatabaseConstants.KeepUntil} <= (now() at time zone 'utc')");
            builder.StartNewCommand();
            builder.Append($"delete from {_queue.ScheduledTable.Identifier} where {DatabaseConstants.KeepUntil} IS NOT NULL and {DatabaseConstants.KeepUntil} <= (now() at time zone 'utc')");
            var batch = builder.Compile();

            batch.Connection = conn;

            await batch.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public Task StartAsync()
    {
        _task = Task.Run(listenForMessagesAsync, _cancellation.Token);
        _scheduledTask = Task.Run(lookForScheduledMessagesAsync, _cancellation.Token);

        return Task.CompletedTask;
    }
}