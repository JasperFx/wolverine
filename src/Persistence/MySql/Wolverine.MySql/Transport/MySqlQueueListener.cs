using JasperFx.Core;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Weasel.MySql;
using Wolverine.Configuration;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.MySql.Transport;

internal class MySqlQueueListener : IListener
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly MySqlQueue _queue;
    private readonly IReceiver _receiver;
    private readonly MySqlDataSource _dataSource;
    private readonly string? _databaseName;
    private readonly ILogger<MySqlQueueListener> _logger;
    private Task? _task;
    private readonly DurabilitySettings _settings;
    private Task? _scheduledTask;
    private readonly MySqlQueueSender _sender;
    private readonly string _tryPopMessagesDirectlySql;
    private readonly string _queueTableName;
    private readonly string _queueName;
    private readonly string _schemaName;
    private readonly string _scheduledTableName;
    private readonly TimeSpan _pollingInterval;
    private readonly bool _deterministicQueueOrdering;
    private readonly string _queueOrderBy;
    private readonly string _scheduledOrderBy;
    private readonly string _tempPopTable;
    private readonly string _tempMoveTable;

    public MySqlQueueListener(MySqlQueue queue, IWolverineRuntime runtime, IReceiver receiver,
        MySqlDataSource dataSource, string? databaseName)
    {
        Address = MySqlQueue.ToUri(queue.Name, databaseName);
        _queue = queue;
        _receiver = receiver;
        _dataSource = dataSource;
        _databaseName = databaseName;
        _logger = runtime.LoggerFactory.CreateLogger<MySqlQueueListener>();
        _settings = runtime.DurabilitySettings;
        _pollingInterval = queue.PollingInterval ?? _settings.ScheduledJobPollingTime;

        _sender = new MySqlQueueSender(queue, _dataSource, databaseName);

        // GH-3859: resolved against this listener's own data source -- on a multi-tenanted host each
        // tenant's queue tables live in that tenant's database.
        _queueTableName = _queue.QueueTableFor(dataSource).Identifier.QualifiedName;
        _scheduledTableName = _queue.ScheduledTableFor(dataSource).Identifier.QualifiedName;
        _schemaName = _queue.Parent.MessageStorageSchemaName;
        _queueName = _queue.Name;

        // GH-4862: the queue_order tie-breaker is only referenced when its schema is enabled. Without it,
        // rows are still handed over in timestamp (and execution_time) order, but ties stay arbitrary.
        _deterministicQueueOrdering = queue.Parent.EnableDeterministicQueueOrdering;
        var tieBreaker = _deterministicQueueOrdering ? $", {QueueOrdering.QueueOrderColumn}" : string.Empty;
        _queueOrderBy = $"timestamp{tieBreaker}";
        _scheduledOrderBy = $"{DatabaseConstants.ExecutionTime}{tieBreaker}";

        // These temporary tables carry the ordering columns so the batch can be read back in order. They
        // have new names because a pooled connection keeps the temporary tables of an earlier session,
        // and CREATE TEMPORARY TABLE IF NOT EXISTS would silently reuse the older temp_pop_* /
        // temp_move_* shape that has no ordering columns.
        _tempPopTable = $"temp_pop_ordered_{_queueName}";
        _tempMoveTable = $"temp_move_ordered_{_queueName}";

        // MySQL doesn't have CTID, so we use a different approach:
        // Select rows with FOR UPDATE SKIP LOCKED, delete them, and return the bodies
        _tryPopMessagesDirectlySql = $@"
SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body} FROM {_queueTableName}
ORDER BY {_queueOrderBy} LIMIT @count FOR UPDATE SKIP LOCKED
";
    }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    // The queue_order column only exists once deterministic ordering has been enabled
    private string queueOrderOrNull() => _deterministicQueueOrdering ? QueueOrdering.QueueOrderColumn : "NULL";

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
        _task.SafeDispose();
        _scheduledTask.SafeDispose();
    }

    public Uri Address { get; }

    public async ValueTask StopAsync()
    {
        await _cancellation.CancelAsync();

        _task?.SafeDispose();
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
                    _logger.LogInformation("Propagated {Number} scheduled messages to MySQL-backed queue {Queue}", count, _queueName);
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

                _logger.LogError(e, "Error while trying to propagate scheduled messages from MySQL Queue {Name}",
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
            // MySQL approach: use a transaction with temp table
            await using var transaction = await conn.BeginTransactionAsync(cancellationToken);

            MySqlCommand CreateCmd(string sql)
            {
                var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = sql;
                return cmd;
            }

            // Create temp table to hold the rows to move, with the columns they are ordered by
            await using var createTempCmd = CreateCmd($@"
CREATE TEMPORARY TABLE IF NOT EXISTS {_tempMoveTable} (
    id CHAR(36) PRIMARY KEY,
    body LONGBLOB,
    message_type VARCHAR(500),
    keep_until DATETIME(6),
    {DatabaseConstants.ExecutionTime} DATETIME(6),
    {QueueOrdering.QueueOrderColumn} BIGINT NULL
)");
            await createTempCmd.ExecuteNonQueryAsync(cancellationToken);

            // Clear any existing data
            await using var truncateCmd = CreateCmd($"TRUNCATE TABLE {_tempMoveTable}");
            await truncateCmd.ExecuteNonQueryAsync(cancellationToken);

            // Select scheduled messages that are ready
            await using var selectCmd = CreateCmd($@"
INSERT INTO {_tempMoveTable} (id, body, message_type, keep_until, {DatabaseConstants.ExecutionTime}, {QueueOrdering.QueueOrderColumn})
SELECT id, body, message_type, keep_until, {DatabaseConstants.ExecutionTime}, {queueOrderOrNull()}
FROM {_scheduledTableName}
WHERE {DatabaseConstants.ExecutionTime} <= UTC_TIMESTAMP(6)
AND id NOT IN (SELECT id FROM {_queueTableName})
ORDER BY {_scheduledOrderBy}
FOR UPDATE SKIP LOCKED");
            await selectCmd.ExecuteNonQueryAsync(cancellationToken);

            // Insert into queue table in execution order. INSERT ... SELECT ... ORDER BY assigns
            // AUTO_INCREMENT values in that order, so the ready table's queue_order keeps it.
            await using var insertCmd = CreateCmd($@"
INSERT INTO {_queueTableName} (id, body, message_type, keep_until)
SELECT id, body, message_type, keep_until FROM {_tempMoveTable}
ORDER BY {_scheduledOrderBy}");
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);

            // Delete from scheduled table
            await using var deleteCmd = CreateCmd($@"
DELETE FROM {_scheduledTableName}
WHERE id IN (SELECT id FROM {_tempMoveTable})");
            await deleteCmd.ExecuteNonQueryAsync(cancellationToken);

            // Get count
            await using var countCmd = CreateCmd($"SELECT COUNT(*) FROM {_tempMoveTable}");
            var countResult = await countCmd.ExecuteScalarAsync(cancellationToken);
            count = Convert.ToInt64(countResult);

            await transaction.CommitAsync(cancellationToken);
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

                    if (messages.Count > _queue.MaximumMessagesToReceive)
                    {
                        await Task.Delay(250.Milliseconds());
                    }
                    else
                    {
                        await Task.Delay(_pollingInterval);
                    }
                }
                else
                {
                    // Slow down if this is a periodically used queue
                    await Task.Delay(_pollingInterval);
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

                _logger.LogError(e, "Error while trying to retrieve messages from MySQL Queue {Name}",
                    _queueName);

                await Task.Delay(pauseTime);
            }
        }
    }

    public async Task<IReadOnlyList<Envelope>> TryPopDurablyAsync(int count, DurabilitySettings settings,
        ILogger logger, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        try
        {
            await using var transaction = await conn.BeginTransactionAsync(cancellationToken);

            MySqlCommand CreateCmd(string sql)
            {
                var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = sql;
                return cmd;
            }

            // First, delete any messages that are already in the incoming table (deduplication).
            // GH-4316: rows this can legitimately hit were put in the inbox by this queue's own
            // earlier pop, which always stamps received_at with this listener's address — scope
            // the probe instead of correlating against the entire inbox.
            await using var dedupCmd = CreateCmd($"DELETE FROM {_queueTableName} WHERE id IN (SELECT id FROM {_schemaName}.{DatabaseConstants.IncomingTable} WHERE {DatabaseConstants.ReceivedAt} = @receivedAt)");
            dedupCmd.Parameters.AddWithValue("@receivedAt", Address.ToString());
            await dedupCmd.ExecuteNonQueryAsync(cancellationToken);

            // Create temp table for this operation
            await using var createTempCmd = CreateCmd($@"
CREATE TEMPORARY TABLE IF NOT EXISTS {_tempPopTable} (
    id CHAR(36) PRIMARY KEY,
    body LONGBLOB,
    message_type VARCHAR(500),
    keep_until DATETIME(6),
    timestamp DATETIME(6),
    {QueueOrdering.QueueOrderColumn} BIGINT NULL
)");
            await createTempCmd.ExecuteNonQueryAsync(cancellationToken);

            // Clear temp table
            await using var truncateCmd = CreateCmd($"TRUNCATE TABLE {_tempPopTable}");
            await truncateCmd.ExecuteNonQueryAsync(cancellationToken);

            // Select messages to process with lock
            await using var selectCmd = CreateCmd($@"
INSERT INTO {_tempPopTable} (id, body, message_type, keep_until, timestamp, {QueueOrdering.QueueOrderColumn})
SELECT id, body, message_type, keep_until, timestamp, {queueOrderOrNull()}
FROM {_queueTableName}
ORDER BY {_queueOrderBy}
LIMIT @count
FOR UPDATE SKIP LOCKED");
            selectCmd.Parameters.AddWithValue("@count", count);
            await selectCmd.ExecuteNonQueryAsync(cancellationToken);

            // Delete from queue table
            await using var deleteCmd = CreateCmd($"DELETE FROM {_queueTableName} WHERE id IN (SELECT id FROM {_tempPopTable})");
            await deleteCmd.ExecuteNonQueryAsync(cancellationToken);

            // Insert into incoming table
            await using var insertCmd = CreateCmd($@"
INSERT INTO {_schemaName}.{DatabaseConstants.IncomingTable} (id, status, owner_id, body, message_type, received_at, keep_until)
SELECT id, 'Incoming', @ownerId, body, message_type, @receivedAt, keep_until
FROM {_tempPopTable}");
            insertCmd.Parameters.AddWithValue("@ownerId", settings.AssignedNodeNumber);
            insertCmd.Parameters.AddWithValue("@receivedAt", Address.ToString());
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);

            // Read the bodies. GH-4862: without the ORDER BY these came back in the temporary table's
            // primary key (message id) order, not the order they were selected in.
            var list = new List<Envelope>();
            await using var readCmd = CreateCmd($"SELECT body FROM {_tempPopTable} ORDER BY {_queueOrderBy}");
            await using var reader = await readCmd.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var data = (byte[])reader.GetValue(0);
                try
                {
                    var e = EnvelopeSerializer.Deserialize(data);
                    list.Add(e);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error trying to deserialize Envelope data in MySQL Transport Queue {Queue}, discarding", _queueName);
                    var ping = Envelope.ForPing(Address); // just a stand in
                    list.Add(ping);
                }
            }

            await reader.CloseAsync();
            await transaction.CommitAsync(cancellationToken);

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
            await using var transaction = await conn.BeginTransactionAsync(cancellationToken);

            MySqlCommand CreateCmd(string sql)
            {
                var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = sql;
                return cmd;
            }

            // Select messages with lock
            var idsToDelete = new List<Guid>();
            var list = new List<Envelope>();

            await using var selectCmd = CreateCmd(_tryPopMessagesDirectlySql);
            selectCmd.Parameters.AddWithValue("@count", count);
            await using (var reader = await selectCmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var id = reader.GetGuid(0);
                    var data = (byte[])reader.GetValue(1);

                    idsToDelete.Add(id);

                    try
                    {
                        var e = EnvelopeSerializer.Deserialize(data);
                        list.Add(e);
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "Error trying to deserialize Envelope data in MySQL Transport Queue {Queue}, discarding", _queueName);
                        var ping = Envelope.ForPing(Address); // just a stand in
                        list.Add(ping);
                    }
                }
            }

            // Delete the messages we just read
            if (idsToDelete.Any())
            {
                // Build parameterized IN clause
                var paramNames = new string[idsToDelete.Count];
                await using var cmd = CreateCmd("");
                for (int i = 0; i < idsToDelete.Count; i++)
                {
                    paramNames[i] = $"@id{i}";
                    cmd.Parameters.AddWithValue(paramNames[i], idsToDelete[i]);
                }
                cmd.CommandText = $"DELETE FROM {_queueTableName} WHERE id IN ({string.Join(", ", paramNames)})";
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            return list;
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
            await conn.CreateCommand($@"
DELETE FROM {_queueTableName}
WHERE {DatabaseConstants.KeepUntil} IS NOT NULL
AND {DatabaseConstants.KeepUntil} <= UTC_TIMESTAMP(6)")
                .ExecuteNonQueryAsync(cancellationToken);

            await conn.CreateCommand($@"
DELETE FROM {_scheduledTableName}
WHERE {DatabaseConstants.KeepUntil} IS NOT NULL
AND {DatabaseConstants.KeepUntil} <= UTC_TIMESTAMP(6)")
                .ExecuteNonQueryAsync(cancellationToken);
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

        _task = Task.Run(listenForMessagesAsync, _cancellation.Token);
        _scheduledTask = Task.Run(lookForScheduledMessagesAsync, _cancellation.Token);
    }
}
