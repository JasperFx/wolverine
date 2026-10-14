using System.Runtime.ExceptionServices;
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
    private readonly bool _deterministicQueueOrdering;
    private readonly string _queueNotificationKey;
    private IAsyncDisposable? _notificationRegistration;

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
        _deterministicQueueOrdering = queue.Parent.EnableDeterministicQueueOrdering;

        _sender = new PostgresqlQueueSender(queue, _dataSource, databaseName, _logger);

        _queueTableName = _queue.QueueTable.Identifier.QualifiedName;
        _scheduledTableName = _queue.ScheduledTable.Identifier.QualifiedName;
        _quotedSchemaName = _queue.Parent.MessageStorageSchemaName.QuoteIdentifier();
        _queueNotificationKey = PostgresqlQueueNotification.Key(queue);

        // DELETE ... RETURNING does not preserve the ordering of the selected rows. Always reapply
        // timestamp order; the sequence tie-breaker is only referenced when its schema is enabled.
        var queueOrderInOrderBy = _deterministicQueueOrdering ? $", {_queueTableName}.{QueueTable.QueueOrderColumn}" : string.Empty;
        var queueOrderInReturning = _deterministicQueueOrdering ? $", {QueueTable.QueueOrderColumn}" : string.Empty;
        var queueOrderInResultOrderBy = _deterministicQueueOrdering ? $", message.{QueueTable.QueueOrderColumn}" : string.Empty;
        _tryPopMessagesDirectlySql = $@"
WITH message AS (
               DELETE
               FROM {_queueTableName}
               WHERE CTID IN (
                   SELECT ctid FROM {_queueTableName}
                   ORDER BY {_queueTableName}.timestamp{queueOrderInOrderBy}
                   LIMIT :COUNT FOR UPDATE SKIP LOCKED
               )
               RETURNING {DatabaseConstants.Body}, timestamp{queueOrderInReturning}
)
SELECT message.{DatabaseConstants.Body} from message ORDER BY message.timestamp{queueOrderInResultOrderBy};
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
        await releaseNotificationRegistrationAsync();
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
        await releaseNotificationRegistrationAsync();

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
            var scheduledOrder = _deterministicQueueOrdering
                ? $", {QueueTable.QueueOrderColumn}"
                : string.Empty;
            var scheduledReturning = _deterministicQueueOrdering
                ? $", {DatabaseConstants.ExecutionTime}, {QueueTable.QueueOrderColumn}"
                : string.Empty;
            var promotionOrder = _deterministicQueueOrdering
                ? $"ORDER BY {DatabaseConstants.ExecutionTime}, {QueueTable.QueueOrderColumn}"
                : string.Empty;
            var parameters = builder.AppendWithParameters($@"
WITH moved AS (
    DELETE FROM {_scheduledTableName} WHERE CTID IN (
        SELECT ctid FROM {_scheduledTableName}
        WHERE {DatabaseConstants.ExecutionTime} <= now()
          AND id NOT IN (SELECT id FROM {_queueTableName})
        ORDER BY {DatabaseConstants.ExecutionTime}{scheduledOrder}
        LIMIT ? FOR UPDATE SKIP LOCKED
    )
    RETURNING id, body, message_type, keep_until{scheduledReturning}
), promoted AS (
    INSERT INTO {_queueTableName} (id, body, message_type, keep_until)
    SELECT id, body, message_type, keep_until FROM moved
    {promotionOrder}
)
SELECT count(*) FROM moved");

            parameters[0].Value = _settings.RecoveryBatchSize;
            parameters[0].NpgsqlDbType = NpgsqlDbType.Integer;

            await using var batch = builder.Compile();
            batch.Connection = conn;

            count = (long)(await batch
                .ExecuteScalarAsync(cancellationToken))!;

            if (count > 0 && _queue.Parent.EnableQueueNotifications)
            {
                await PostgresqlQueueNotification.PublishAsync(conn, _queueNotificationKey, _logger,
                    cancellationToken);
            }
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

        // Preserve the original post-process pacing, but let a queue notification start the next poll immediately.
        await _loop!.WaitForWakeupOrTimeoutAsync(
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
        // The ORDER BY in the row selection only chooses the batch; DELETE ... RETURNING does not
        // keep that order. The persistent sequence is included only for installations that opted in.
        var queueOrderInOrderBy = _deterministicQueueOrdering
            ? $", {_queueTableName}.{QueueTable.QueueOrderColumn}"
            : string.Empty;
        var queueOrderInReturning = _deterministicQueueOrdering
            ? $", {QueueTable.QueueOrderColumn}"
            : string.Empty;
        var queueOrderInResultOrderBy = _deterministicQueueOrdering
            ? $", popped.{QueueTable.QueueOrderColumn}"
            : string.Empty;
        var parameters = builder.AppendWithParameters($@"
WITH popped AS (
    DELETE FROM {_queueTableName}
    WHERE CTID IN (
        SELECT ctid FROM {_queueTableName}
        ORDER BY {_queueTableName}.timestamp{queueOrderInOrderBy}
        LIMIT ? FOR UPDATE SKIP LOCKED
    )
    RETURNING {DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}, timestamp{queueOrderInReturning}
), inserted AS (
    INSERT INTO {_quotedSchemaName}.{DatabaseConstants.IncomingTable}
        (id, status, owner_id, body, message_type, received_at, keep_until)
    SELECT id, 'Incoming', ?, body, message_type, '{Address}', keep_until FROM popped
)
SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body} FROM popped ORDER BY popped.timestamp{queueOrderInResultOrderBy}");

        parameters[0].Value = count;
        parameters[0].NpgsqlDbType = NpgsqlDbType.Integer;
        parameters[1].Value = settings.AssignedNodeNumber;
        parameters[1].NpgsqlDbType = NpgsqlDbType.Integer;

        await using var batch = builder.Compile();

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        var batchIds = new List<Guid>();
        var list = new List<Envelope>();
        var resultWasMaterialized = false;
        Exception? failureAfterMaterialization = null;

        try
        {
            // Keep the queue delete and inbox insert uncommitted until the client has read and
            // materialized the complete result. With an implicit transaction, PostgreSQL can commit
            // the pop before the client receives the response; a read timeout then strands inbox rows
            // under this live node because the listener never received the envelopes to process. If
            // the later COMMIT reply is lost, the batch is released to owner_id = 0 for inbox recovery.
            await using var transaction = await conn.BeginTransactionAsync(cancellationToken);
            batch.Connection = conn;
            batch.Transaction = transaction;
            await using var reader = await batch.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = await reader.GetFieldValueAsync<Guid>(0, cancellationToken);
                batchIds.Add(id);
                var data = await reader.GetFieldValueAsync<byte[]>(1, cancellationToken);
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
            resultWasMaterialized = true;
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception e) when (resultWasMaterialized)
        {
            // The server may have committed even when the client did not receive COMMIT's reply. The
            // transaction is disposed before this catch; the finally block closes the connection before
            // reconciliation inspects the committed state on a fresh connection.
            failureAfterMaterialization = e;
        }
        finally
        {
            try
            {
                await conn.CloseAsync();
            }
            catch (Exception e) when (resultWasMaterialized)
            {
                failureAfterMaterialization ??= e;
            }
        }

        if (failureAfterMaterialization != null)
        {
            try
            {
                await releaseCommittedBatchForRecoveryAsync(batchIds, settings.AssignedNodeNumber, logger);
            }
            catch (Exception e)
            {
                logger.LogError(e,
                    "Error releasing PostgreSQL queue batch {Queue} for recovery after a pop commit failure",
                    _queueName);
            }

            ExceptionDispatchInfo.Capture(failureAfterMaterialization).Throw();
        }

        return list;
    }

    private async Task releaseCommittedBatchForRecoveryAsync(IReadOnlyList<Guid> ids, int ownerId, ILogger logger)
    {
        if (ids.Count == 0) return;

        await using var conn = await _dataSource.OpenConnectionAsync(CancellationToken.None);
        try
        {
            var released = await conn.CreateCommand($@"
UPDATE {_quotedSchemaName}.{DatabaseConstants.IncomingTable}
SET {DatabaseConstants.OwnerId} = 0
WHERE {DatabaseConstants.Id} = ANY(@ids)
  AND {DatabaseConstants.Status} = 'Incoming'
  AND {DatabaseConstants.OwnerId} = @owner
  AND {DatabaseConstants.ReceivedAt} = @address")
                .With("ids", ids.ToArray())
                .With("owner", ownerId)
                .With("address", Address.ToString())
                .ExecuteNonQueryAsync(CancellationToken.None);

            if (released > 0)
            {
                logger.LogWarning(
                    "Released {Count} committed messages from PostgreSQL queue {Queue} for inbox recovery after a post-read commit failure",
                    released, _queueName);
            }
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
            builder.Append($"delete from {_queueTableName} where {DatabaseConstants.KeepUntil} IS NOT NULL and {DatabaseConstants.KeepUntil} <= now()");
            builder.StartNewCommand();
            builder.Append($"delete from {_queue.ScheduledTable.Identifier} where {DatabaseConstants.KeepUntil} IS NOT NULL and {DatabaseConstants.KeepUntil} <= now()");
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

        if (_queue.Parent.EnableQueueNotifications)
        {
            _notificationRegistration = await _queue.Parent.QueueNotificationManager.RegisterAsync(
                _dataSource, _databaseName ?? "default", _queueNotificationKey, () => _loop?.Wake(), _logger,
                _cancellation.Token);
        }

        _loop = new BackgroundReceiveLoop(Address, _logger, pollOnceAsync, _cancellation.Token, _pollingInterval);
        _loop.Start();
        _scheduledTask = Task.Run(lookForScheduledMessagesAsync, _cancellation.Token);
    }

    private async ValueTask releaseNotificationRegistrationAsync()
    {
        var registration = Interlocked.Exchange(ref _notificationRegistration, null);
        if (registration != null)
        {
            await registration.DisposeAsync();
        }
    }
}
