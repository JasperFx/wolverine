using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;
using Weasel.Oracle;
using Wolverine.Configuration;
using Wolverine.Oracle.Util;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.Oracle.Transport;

internal class OracleQueueListener : IListener
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly OracleQueue _queue;
    private readonly IReceiver _receiver;
    private readonly OracleDataSource _dataSource;
    private readonly string? _databaseName;
    private readonly ILogger<OracleQueueListener> _logger;
    private Task? _task;
    private readonly DurabilitySettings _settings;
    private Task? _scheduledTask;
    private readonly OracleQueueSender _sender;
    private readonly string _tryPopMessagesDirectlySql;
    private readonly string _queueTableName;
    private readonly string _queueName;
    private readonly string _schemaName;
    private readonly string _scheduledTableName;

    public OracleQueueListener(OracleQueue queue, IWolverineRuntime runtime, IReceiver receiver,
        OracleDataSource dataSource, string? databaseName)
    {
        Address = OracleQueue.ToUri(queue.Name, databaseName);
        _queue = queue;
        _receiver = receiver;
        _dataSource = dataSource;
        _databaseName = databaseName;
        _logger = runtime.LoggerFactory.CreateLogger<OracleQueueListener>();
        _settings = runtime.DurabilitySettings;

        _sender = new OracleQueueSender(queue, _dataSource, databaseName);

        _queueTableName = _queue.QueueTable.Identifier.QualifiedName;
        _scheduledTableName = _queue.ScheduledTable.Identifier.QualifiedName;
        _schemaName = _queue.Parent.MessageStorageSchemaName;

        _tryPopMessagesDirectlySql =
            $"SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body} FROM {_queueTableName} " +
            "ORDER BY timestamp FETCH FIRST :count ROWS ONLY FOR UPDATE SKIP LOCKED";

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
        await Task.Delay(_settings.ScheduledJobFirstExecution);

        var failedCount = 0;

        while (!_cancellation.Token.IsCancellationRequested)
        {
            try
            {
                var count = await MoveScheduledToReadyQueueAsync(_cancellation.Token);
                if (count > 0)
                {
                    _logger.LogInformation("Propagated {Number} scheduled messages to Oracle-backed queue {Queue}", count, _queueName);
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

                _logger.LogError(e, "Error while trying to propagate scheduled messages from Oracle Queue {Name}",
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
            await using var transaction = await conn.BeginTransactionAsync(cancellationToken);

            OracleCommand CreateCmd(string sql)
            {
                var cmd = conn.CreateCommand(sql);
                cmd.Transaction = (OracleTransaction)transaction;
                return cmd;
            }

            // Select scheduled messages that are ready and lock them
            var selectCmd = CreateCmd(
                $"SELECT id, body, message_type, keep_until FROM {_scheduledTableName} " +
                $"WHERE {DatabaseConstants.ExecutionTime} <= SYS_EXTRACT_UTC(SYSTIMESTAMP) " +
                "FOR UPDATE SKIP LOCKED");

            var idsToMove = new List<Guid>();
            var bodies = new List<byte[]>();
            var messageTypes = new List<string>();
            var keepUntils = new List<DateTimeOffset?>();

            await using (var reader = await selectCmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    idsToMove.Add(await OracleEnvelopeReader.ReadGuidAsync(reader, 0, cancellationToken));
                    bodies.Add((byte[])reader.GetValue(1));
                    messageTypes.Add(await reader.GetFieldValueAsync<string>(2, cancellationToken));
                    keepUntils.Add(await reader.IsDBNullAsync(3, cancellationToken) ? null : await reader.GetFieldValueAsync<DateTimeOffset>(3, cancellationToken));
                }
            }

            for (int i = 0; i < idsToMove.Count; i++)
            {
                // Insert into queue (skip if already exists)
                try
                {
                    var insertCmd = CreateCmd(
                        $"INSERT INTO {_queueTableName} (id, body, message_type, keep_until) VALUES (:id, :body, :type, :keepUntil)");
                    insertCmd.With("id", idsToMove[i]);
                    insertCmd.Parameters.Add(new OracleParameter("body", OracleDbType.Blob) { Value = bodies[i] });
                    insertCmd.With("type", messageTypes[i]);
                    insertCmd.Parameters.Add(new OracleParameter("keepUntil", OracleDbType.TimeStampTZ) { Value = (object?)keepUntils[i] ?? DBNull.Value });
                    await insertCmd.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (OracleException ex) when (ex.Number == 1)
                {
                    // Already exists in queue, skip
                }

                // Delete from scheduled
                var deleteCmd = CreateCmd(
                    $"DELETE FROM {_scheduledTableName} WHERE id = :id");
                deleteCmd.With("id", idsToMove[i]);
                await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            count = idsToMove.Count;

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

                _logger.LogError(e, "Error while trying to retrieve messages from Oracle Queue {Name}",
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

            OracleCommand CreateCmd(string sql)
            {
                var cmd = conn.CreateCommand(sql);
                cmd.Transaction = (OracleTransaction)transaction;
                return cmd;
            }

            // First, delete any messages that are already in the incoming table (deduplication)
            await CreateCmd($"DELETE FROM {_queueTableName} WHERE id IN (SELECT id FROM {_schemaName}.{DatabaseConstants.IncomingTable})")
                .ExecuteNonQueryAsync(cancellationToken);

            // Select messages to process with lock
            var selectCmd = CreateCmd(
                $"SELECT id, body, message_type, keep_until FROM {_queueTableName} " +
                "ORDER BY timestamp FETCH FIRST :count ROWS ONLY FOR UPDATE SKIP LOCKED");
            selectCmd.With("count", count);

            var ids = new List<Guid>();
            var bodyList = new List<byte[]>();
            var messageTypeList = new List<string>();
            var keepUntilList = new List<DateTimeOffset?>();

            await using (var reader = await selectCmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    ids.Add(await OracleEnvelopeReader.ReadGuidAsync(reader, 0, cancellationToken));
                    bodyList.Add((byte[])reader.GetValue(1));
                    messageTypeList.Add(await reader.GetFieldValueAsync<string>(2, cancellationToken));
                    keepUntilList.Add(await reader.IsDBNullAsync(3, cancellationToken) ? null : await reader.GetFieldValueAsync<DateTimeOffset>(3, cancellationToken));
                }
            }

            if (ids.Count == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Array.Empty<Envelope>();
            }

            var list = new List<Envelope>();

            for (int i = 0; i < ids.Count; i++)
            {
                // Delete from queue
                var deleteCmd = CreateCmd($"DELETE FROM {_queueTableName} WHERE id = :id");
                deleteCmd.With("id", ids[i]);
                await deleteCmd.ExecuteNonQueryAsync(cancellationToken);

                // Insert into incoming
                var insertCmd = CreateCmd(
                    $"INSERT INTO {_schemaName}.{DatabaseConstants.IncomingTable} (id, status, owner_id, body, message_type, received_at, keep_until) " +
                    "VALUES (:id, 'Incoming', :ownerId, :body, :messageType, :receivedAt, :keepUntil)");
                insertCmd.With("id", ids[i]);
                insertCmd.With("ownerId", settings.AssignedNodeNumber);
                insertCmd.Parameters.Add(new OracleParameter("body", OracleDbType.Blob) { Value = bodyList[i] });
                insertCmd.With("messageType", messageTypeList[i]);
                insertCmd.With("receivedAt", Address.ToString());
                insertCmd.Parameters.Add(new OracleParameter("keepUntil", OracleDbType.TimeStampTZ) { Value = (object?)keepUntilList[i] ?? DBNull.Value });
                await insertCmd.ExecuteNonQueryAsync(cancellationToken);

                try
                {
                    var e = EnvelopeSerializer.Deserialize(bodyList[i]);
                    list.Add(e);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error trying to deserialize Envelope data in Oracle Transport Queue {Queue}, discarding", _queueName);
                    var ping = Envelope.ForPing(Address);
                    list.Add(ping);
                }
            }

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

            OracleCommand CreateCmd(string sql)
            {
                var cmd = conn.CreateCommand(sql);
                cmd.Transaction = (OracleTransaction)transaction;
                return cmd;
            }

            var idsToDelete = new List<Guid>();
            var list = new List<Envelope>();

            var selectCmd = CreateCmd(_tryPopMessagesDirectlySql);
            selectCmd.With("count", count);
            await using (var reader = await selectCmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var id = await OracleEnvelopeReader.ReadGuidAsync(reader, 0, cancellationToken);
                    var data = (byte[])reader.GetValue(1);

                    idsToDelete.Add(id);

                    try
                    {
                        var e = EnvelopeSerializer.Deserialize(data);
                        list.Add(e);
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e, "Error trying to deserialize Envelope data in Oracle Transport Queue {Queue}, discarding", _queueName);
                        var ping = Envelope.ForPing(Address);
                        list.Add(ping);
                    }
                }
            }

            // Delete the messages we just read
            foreach (var id in idsToDelete)
            {
                var deleteCmd = CreateCmd($"DELETE FROM {_queueTableName} WHERE id = :id");
                deleteCmd.With("id", id);
                await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
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
            var cmd1 = conn.CreateCommand(
                $"DELETE FROM {_queueTableName} WHERE {DatabaseConstants.KeepUntil} IS NOT NULL AND {DatabaseConstants.KeepUntil} <= SYS_EXTRACT_UTC(SYSTIMESTAMP)");
            await cmd1.ExecuteNonQueryAsync(cancellationToken);

            var cmd2 = conn.CreateCommand(
                $"DELETE FROM {_scheduledTableName} WHERE {DatabaseConstants.KeepUntil} IS NOT NULL AND {DatabaseConstants.KeepUntil} <= SYS_EXTRACT_UTC(SYSTIMESTAMP)");
            await cmd2.ExecuteNonQueryAsync(cancellationToken);
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
