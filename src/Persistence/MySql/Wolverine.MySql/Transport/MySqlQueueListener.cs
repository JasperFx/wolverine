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

        _sender = new MySqlQueueSender(queue, _dataSource, databaseName);

        _queueTableName = _queue.QueueTable.Identifier.QualifiedName;
        _scheduledTableName = _queue.ScheduledTable.Identifier.QualifiedName;
        _schemaName = _queue.Parent.MessageStorageSchemaName;

        // MySQL doesn't have CTID, so we use a different approach:
        // Select rows with FOR UPDATE SKIP LOCKED, delete them, and return the bodies
        _tryPopMessagesDirectlySql = $@"
SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body} FROM {_queueTableName}
ORDER BY timestamp LIMIT @count FOR UPDATE SKIP LOCKED
";
        _queueName = _queue.Name;
    }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

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
                    _logger.LogInformation("Propagated {Number} scheduled messages to MySQL-backed queue {Queue}", count, _queueName);
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

            // Create temp table to hold IDs to move
            await CreateCmd($@"
CREATE TEMPORARY TABLE IF NOT EXISTS temp_move_{_queueName} (
    id CHAR(36) PRIMARY KEY,
    body LONGBLOB,
    message_type VARCHAR(500),
    keep_until DATETIME(6)
)")
                .ExecuteNonQueryAsync(cancellationToken);

            // Clear any existing data
            await CreateCmd($"TRUNCATE TABLE temp_move_{_queueName}")
                .ExecuteNonQueryAsync(cancellationToken);

            // Select scheduled messages that are ready
            await CreateCmd($@"
INSERT INTO temp_move_{_queueName} (id, body, message_type, keep_until)
SELECT id, body, message_type, keep_until
FROM {_scheduledTableName}
WHERE {DatabaseConstants.ExecutionTime} <= UTC_TIMESTAMP(6)
AND id NOT IN (SELECT id FROM {_queueTableName})
FOR UPDATE SKIP LOCKED")
                .ExecuteNonQueryAsync(cancellationToken);

            // Insert into queue table
            await CreateCmd($@"
INSERT INTO {_queueTableName} (id, body, message_type, keep_until)
SELECT id, body, message_type, keep_until FROM temp_move_{_queueName}")
                .ExecuteNonQueryAsync(cancellationToken);

            // Delete from scheduled table
            await CreateCmd($@"
DELETE FROM {_scheduledTableName}
WHERE id IN (SELECT id FROM temp_move_{_queueName})")
                .ExecuteNonQueryAsync(cancellationToken);

            // Get count
            var countResult = await CreateCmd($"SELECT COUNT(*) FROM temp_move_{_queueName}")
                .ExecuteScalarAsync(cancellationToken);
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
                        await Task.Delay(_settings.ScheduledJobPollingTime);
                    }
                }
                else
                {
                    // Slow down if this is a periodically used queue
                    await Task.Delay(_settings.ScheduledJobPollingTime);
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

            // First, delete any messages that are already in the incoming table (deduplication)
            await CreateCmd($"DELETE FROM {_queueTableName} WHERE id IN (SELECT id FROM {_schemaName}.{DatabaseConstants.IncomingTable})")
                .ExecuteNonQueryAsync(cancellationToken);

            // Create temp table for this operation
            await CreateCmd($@"
CREATE TEMPORARY TABLE IF NOT EXISTS temp_pop_{_queueName} (
    id CHAR(36) PRIMARY KEY,
    body LONGBLOB,
    message_type VARCHAR(500),
    keep_until DATETIME(6)
)")
                .ExecuteNonQueryAsync(cancellationToken);

            // Clear temp table
            await CreateCmd($"TRUNCATE TABLE temp_pop_{_queueName}")
                .ExecuteNonQueryAsync(cancellationToken);

            // Select messages to process with lock
            var selectCmd = CreateCmd($@"
INSERT INTO temp_pop_{_queueName} (id, body, message_type, keep_until)
SELECT id, body, message_type, keep_until
FROM {_queueTableName}
ORDER BY timestamp
LIMIT @count
FOR UPDATE SKIP LOCKED");
            selectCmd.Parameters.AddWithValue("@count", count);
            await selectCmd.ExecuteNonQueryAsync(cancellationToken);

            // Delete from queue table
            await CreateCmd($"DELETE FROM {_queueTableName} WHERE id IN (SELECT id FROM temp_pop_{_queueName})")
                .ExecuteNonQueryAsync(cancellationToken);

            // Insert into incoming table
            var insertCmd = CreateCmd($@"
INSERT INTO {_schemaName}.{DatabaseConstants.IncomingTable} (id, status, owner_id, body, message_type, received_at, keep_until)
SELECT id, 'Incoming', @ownerId, body, message_type, @receivedAt, keep_until
FROM temp_pop_{_queueName}");
            insertCmd.Parameters.AddWithValue("@ownerId", settings.AssignedNodeNumber);
            insertCmd.Parameters.AddWithValue("@receivedAt", Address.ToString());
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);

            // Read the bodies
            var list = new List<Envelope>();
            await using var reader = await CreateCmd($"SELECT body FROM temp_pop_{_queueName}")
                .ExecuteReaderAsync(cancellationToken);

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

            var selectCmd = CreateCmd(_tryPopMessagesDirectlySql);
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
                var cmd = CreateCmd("");
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
DELETE FROM {_queue.ScheduledTable.Identifier}
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
