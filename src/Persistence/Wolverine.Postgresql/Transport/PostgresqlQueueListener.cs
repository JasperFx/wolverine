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

internal class PostgresqlQueueListener : IListener, IReportReceiveLoopHealth
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly PostgresqlQueue _queue;
    private readonly IReceiver _receiver;
    private readonly NpgsqlDataSource _dataSource;
    private readonly string? _databaseName;
    private readonly ILogger<PostgresqlQueueListener> _logger;
    // GH-3236: the main poll loop runs on the shared BackgroundReceiveLoop (heartbeat + fault/hung detection).
    private BackgroundReceiveLoop? _loop;
    private readonly DurabilitySettings _settings;
    private Task? _scheduledTask;
    private readonly PostgresqlQueueSender _sender;
    private readonly string _tryPopMessagesDirectlySql;
    private readonly string _queueTableName;
    private readonly string _queueName;
    private readonly string _quotedSchemaName;
    private readonly string _scheduledTableName;
    private readonly TimeSpan _pollingInterval;

    public PostgresqlQueueListener(PostgresqlQueue queue, IWolverineRuntime runtime, IReceiver receiver,
        NpgsqlDataSource dataSource, string? databaseName)
    {
        Address = PostgresqlQueue.ToUri(queue.Name, databaseName);
        _queue = queue;
        _receiver = receiver;
        _dataSource = dataSource;
        _databaseName = databaseName;
        _logger = runtime.LoggerFactory.CreateLogger<PostgresqlQueueListener>();
        _settings = runtime.DurabilitySettings;
        _pollingInterval = queue.PollingInterval ?? _settings.ScheduledJobPollingTime;

        _sender = new PostgresqlQueueSender(queue, _dataSource, databaseName);

        _queueTableName = _queue.QueueTable.Identifier.QualifiedName;
        _scheduledTableName = _queue.ScheduledTable.Identifier.QualifiedName;
        _quotedSchemaName = _queue.Parent.MessageStorageSchemaName.QuoteIdentifier();

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

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    // GH-3236: surface the poll loop's liveness (heartbeat + faulted/hung detection) for EndpointHealthSnapshot.
    public ReceiveLoopStatus ReceiveLoopStatus => _loop?.ReceiveLoopStatus ?? ReceiveLoopStatus.NotStarted;
    public DateTimeOffset? LastReceiveLoopActivityAt => _loop?.LastReceiveLoopActivityAt;

    public ValueTask CompleteAsync(Envelope envelope)
    {
        return ValueTask.CompletedTask;
    }

    public async ValueTask DeferAsync(Envelope envelope)
    {
        await _sender.SendAsync(envelope, _cancellation.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        if (_loop != null)
        {
            await _loop.DisposeAsync();
        }
        _scheduledTask.SafeDispose();
    }

    public Uri Address { get; }

    public async ValueTask StopAsync()
    {
        await _cancellation.CancelAsync();

        if (_loop != null)
        {
            await _loop.StopAsync(_settings.DrainTimeout);
        }
        _scheduledTask?.SafeDispose();
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

                await Task.Delay(_pollingInterval);

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
            // GH-4334: same temp-table removal as the durable pop, plus a bound. The move was
            // unbounded -- every due scheduled message promoted in one statement, which on a large
            // backlog is one enormous transaction holding locks the receive path needs. Capped at
            // RecoveryBatchSize per cycle; the poller runs again immediately after a full batch.
            var builder = new BatchBuilder();
            var parameters = builder.AppendWithParameters($@"
WITH moved AS (
    DELETE FROM {_scheduledTableName} WHERE CTID IN (
        SELECT ctid FROM {_scheduledTableName}
        WHERE {DatabaseConstants.ExecutionTime} <= (now() at time zone 'utc')
          AND id NOT IN (SELECT id FROM {_queueTableName})
        ORDER BY {DatabaseConstants.ExecutionTime}
        LIMIT ? FOR UPDATE SKIP LOCKED
    )
    RETURNING id, body, message_type, keep_until
), promoted AS (
    INSERT INTO {_queueTableName} (id, body, message_type, keep_until)
    SELECT id, body, message_type, keep_until FROM moved
)
SELECT count(*) FROM moved");

            parameters[0].Value = _settings.RecoveryBatchSize;
            parameters[0].NpgsqlDbType = NpgsqlDbType.Integer;

            await using var batch = builder.Compile();
            batch.Connection = conn;

            count = (long)(await batch
                .ExecuteScalarAsync(cancellationToken))!;
        }
        finally
        {
            await conn.CloseAsync();
        }

        return count;
    }

    // One poll-and-process iteration, driven by BackgroundReceiveLoop (which owns the loop task, the
    // log -> backoff -> continue policy on error, the idle delay, the heartbeat, and teardown). Returns true when
    // messages were processed (the busy-path delay below preserves the original pacing), false when idle (the loop
    // applies its idle delay = _pollingInterval).
    private async Task<bool> pollOnceAsync(CancellationToken token)
    {
        var messages = _queue.Mode == EndpointMode.Durable
            ? await TryPopDurablyAsync(_queue.MaximumMessagesToReceive, _settings, _logger, token)
            : await TryPopAsync(_queue.MaximumMessagesToReceive, _logger, token);

        if (!messages.Any())
        {
            return false;
        }

        await _receiver.ReceivedAsync(this, messages.ToArray());

        // Preserve the original post-process pacing between polls.
        await Task.Delay(
            messages.Count > _queue.MaximumMessagesToReceive ? 250.Milliseconds() : _pollingInterval, token);

        return true;
    }

    public async Task<IReadOnlyList<Envelope>> TryPopDurablyAsync(int count, DurabilitySettings settings,
        ILogger logger, CancellationToken cancellationToken)
    {
        var builder = new BatchBuilder();

        // GH-4316: rows this anti-duplicate delete can legitimately hit were put in the inbox by
        // this queue's own earlier pop (a crash between the inbox INSERT below and the queue
        // DELETE), and that INSERT always stamps received_at with this listener's address — so
        // scope the probe instead of correlating against the entire inbox.
        builder.Append($"delete FROM {_queueTableName} where id in (select id from {_quotedSchemaName}.{DatabaseConstants.IncomingTable} where {DatabaseConstants.ReceivedAt} = '{Address}')");
        builder.StartNewCommand();

        // GH-4334: one CTE instead of create-temp-table / delete / insert / select. The old shape
        // created and dropped `temp_pop_{queue}` on EVERY poll of every queue -- a pg_class and
        // pg_attribute insert-and-delete per poll (catalog churn), and a fresh relation each time
        // means the plan is never reused. This is the same DELETE ... RETURNING shape the
        // non-durable pop above already uses, extended with the inbox insert as a second CTE.
        var parameters = builder.AppendWithParameters($@"
WITH popped AS (
    DELETE FROM {_queueTableName} WHERE CTID IN (
        SELECT ctid FROM {_queueTableName} ORDER BY {_queueTableName}.timestamp LIMIT ? FOR UPDATE SKIP LOCKED
    )
    RETURNING {DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}
), inserted AS (
    INSERT INTO {_quotedSchemaName}.{DatabaseConstants.IncomingTable}
        (id, status, owner_id, body, message_type, received_at, keep_until)
    SELECT id, 'Incoming', ?, body, message_type, '{Address}', keep_until FROM popped
)
SELECT {DatabaseConstants.Body} FROM popped");

        parameters[0].Value = count;
        parameters[0].NpgsqlDbType = NpgsqlDbType.Integer;
        parameters[1].Value = settings.AssignedNodeNumber;
        parameters[1].NpgsqlDbType = NpgsqlDbType.Integer;

        await using var batch = builder.Compile();

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
                        logger.LogError(e, "Error trying to deserialize Envelope data in PostgreSQL Transport Queue {Queue}, discarding", _queueName);
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
            await using var batch = builder.Compile();

            batch.Connection = conn;

            await batch.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async Task StartAsync()
    {
        if (_queue.Parent.AutoProvision)
        {
            await _queue.EnsureSchemaExists(_databaseName ?? string.Empty, _dataSource);
        }
        
        _loop = new BackgroundReceiveLoop(Address, _logger, pollOnceAsync, _cancellation.Token, _pollingInterval);
        _loop.Start();
        _scheduledTask = Task.Run(lookForScheduledMessagesAsync, _cancellation.Token);
    }
}