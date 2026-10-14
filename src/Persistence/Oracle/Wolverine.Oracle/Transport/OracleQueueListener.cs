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

internal record UnconfirmedPop(Guid[] Ids, int NodeNumber);

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
    private readonly TimeSpan _pollingInterval;
    private readonly bool _deterministicQueueOrdering;
    private readonly string _queueOrderBy;
    private readonly string _scheduledOrderBy;

    // GH-5015: ids of a durable pop whose COMMIT failed with an unknown outcome and could not be released yet
    private UnconfirmedPop? _unconfirmedPop;

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
        _pollingInterval = queue.PollingInterval ?? _settings.ScheduledJobPollingTime;

        _sender = new OracleQueueSender(queue, _dataSource, databaseName);

        _queueTableName = _queue.QueueTable.Identifier.QualifiedName;
        _scheduledTableName = _queue.ScheduledTable.Identifier.QualifiedName;
        _schemaName = _queue.Parent.MessageStorageSchemaName;
        _deterministicQueueOrdering = queue.Parent.EnableDeterministicQueueOrdering;

        // The identity tie-breaker is only referenced when its schema is enabled
        _queueOrderBy = _deterministicQueueOrdering
            ? $"timestamp, {QueueTable.QueueOrderColumn}"
            : "timestamp";
        _scheduledOrderBy = _deterministicQueueOrdering
            ? $"{DatabaseConstants.ExecutionTime}, {QueueTable.QueueOrderColumn}"
            : DatabaseConstants.ExecutionTime;

        // Oracle refuses ORDER BY + FETCH FIRST + FOR UPDATE (ORA-02014), so the batch size is
        // enforced by reading at most :count rows instead. With SKIP LOCKED, Oracle locks rows
        // as they are fetched rather than when the cursor opens, so this still only claims the
        // rows the batch takes (plus at most one prefetch buffer, released at commit).
        _tryPopMessagesDirectlySql =
            $"SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body} FROM {_queueTableName} " +
            $"ORDER BY {_queueOrderBy} FOR UPDATE SKIP LOCKED";

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

            // Select scheduled messages that are ready and lock them. The rows are inserted into the
            // ready queue one at a time in this order, so their timestamps (and the identity
            // tie-breaker when it is enabled) preserve the scheduled order.
            await using var selectCmd = CreateCmd(
                $"SELECT id, body, message_type, keep_until FROM {_scheduledTableName} " +
                $"WHERE {DatabaseConstants.ExecutionTime} <= SYSTIMESTAMP AT TIME ZONE 'UTC' " +
                $"ORDER BY {_scheduledOrderBy} FOR UPDATE SKIP LOCKED");

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
                    await using var insertCmd = CreateCmd(
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
                await using var deleteCmd = CreateCmd(
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
                if (_unconfirmedPop != null)
                {
                    await ReleaseUnconfirmedPopAsync(_cancellation.Token);
                }

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

            // First, delete any messages that are already in the incoming table (deduplication).
            // GH-4316: rows this can legitimately hit were put in the inbox by this queue's own
            // earlier pop, which always stamps received_at with this listener's address — scope
            // the probe instead of correlating against the entire inbox.
            await using var dedupCmd = CreateCmd($"DELETE FROM {_queueTableName} WHERE id IN (SELECT id FROM {_schemaName}.{DatabaseConstants.IncomingTable} WHERE {DatabaseConstants.ReceivedAt} = :receivedAt)");
            dedupCmd.With("receivedAt", Address.ToString());
            await dedupCmd.ExecuteNonQueryAsync(cancellationToken);

            // Select messages to process with lock. See the constructor for why this reads at most
            // `count` rows rather than using FETCH FIRST
            await using var selectCmd = CreateCmd(
                $"SELECT id, body, message_type, keep_until FROM {_queueTableName} " +
                $"ORDER BY {_queueOrderBy} FOR UPDATE SKIP LOCKED");

            var ids = new List<Guid>();
            var bodyList = new List<byte[]>();
            var messageTypeList = new List<string>();
            var keepUntilList = new List<DateTimeOffset?>();

            await using (var reader = await selectCmd.ExecuteReaderAsync(cancellationToken))
            {
                limitPrefetch(reader, count);
                while (ids.Count < count && await reader.ReadAsync(cancellationToken))
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
                await using var deleteCmd = CreateCmd($"DELETE FROM {_queueTableName} WHERE id = :id");
                deleteCmd.With("id", ids[i]);
                await deleteCmd.ExecuteNonQueryAsync(cancellationToken);

                // Insert into incoming
                await using var insertCmd = CreateCmd(
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

            try
            {
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception e)
            {
                // GH-5015: a COMMIT that fails on the wire may still have committed, leaving the envelopes in the
                // inbox owned by this live node and in no listener's memory -- recovery only reclaims
                // owner_id = 0. Nothing is handed to the receiver (if the move rolled back, the rows are back in
                // the queue and would run twice), so release whatever did commit to recovery instead.
                await releaseAfterFailedCommitAsync(ids, settings.AssignedNodeNumber, e, logger);
                throw;
            }

            return list;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private async Task releaseAfterFailedCommitAsync(List<Guid> ids, int nodeNumber, Exception commitFailure,
        ILogger logger)
    {
        if (ids.Count == 0) return;

        logger.LogError(commitFailure,
            "The commit of a durable pop of {Count} envelope(s) from Oracle queue {Queue} failed with an unknown outcome. Releasing them to recovery",
            ids.Count, _queueName);

        _unconfirmedPop = new UnconfirmedPop(ids.ToArray(), nodeNumber);
        try
        {
            await ReleaseUnconfirmedPopAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogError(e,
                "Unable to release {Count} envelope(s) of an unconfirmed durable pop from Oracle queue {Queue}. Retrying on the next poll",
                ids.Count, _queueName);
        }
    }

    /// <summary>
    /// GH-5015: hand the envelopes of a pop whose COMMIT outcome is unknown to recovery (owner_id = 0). The
    /// SELECT ... FOR UPDATE locks the popped ids in the queue table, which waits out the original transaction
    /// if it is somehow still in flight; once that returns, the move has either committed (the update releases
    /// the inbox rows) or rolled back (the rows are back in the queue and the update matches nothing).
    /// </summary>
    internal async Task ReleaseUnconfirmedPopAsync(CancellationToken cancellationToken)
    {
        if (_unconfirmedPop is not { } pop) return;

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var transaction = await conn.BeginTransactionAsync(cancellationToken);

            var idParameters = pop.Ids.Select((_, i) => ":id" + i).ToArray();
            var ids = string.Join(", ", idParameters);

            OracleCommand createCmd(string sql)
            {
                var cmd = conn.CreateCommand(sql);
                cmd.Transaction = (OracleTransaction)transaction;
                for (var i = 0; i < pop.Ids.Length; i++)
                {
                    cmd.With("id" + i, pop.Ids[i]);
                }

                return cmd;
            }

            await using (var lockCmd = createCmd($"SELECT id FROM {_queueTableName} WHERE id IN ({ids}) FOR UPDATE"))
            {
                await using var reader = await lockCmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                }
            }

            await using (var releaseCmd = createCmd(
                             $"UPDATE {_schemaName}.{DatabaseConstants.IncomingTable} SET {DatabaseConstants.OwnerId} = 0 WHERE id IN ({ids}) AND {DatabaseConstants.OwnerId} = :node AND {DatabaseConstants.Status} = '{EnvelopeStatus.Incoming}'"))
            {
                releaseCmd.With("node", pop.NodeNumber);
                await releaseCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            _unconfirmedPop = null;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    internal bool HasUnconfirmedPop => _unconfirmedPop != null;

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

            await using var selectCmd = CreateCmd(_tryPopMessagesDirectlySql);
            await using (var reader = await selectCmd.ExecuteReaderAsync(cancellationToken))
            {
                limitPrefetch(reader, count);
                while (idsToDelete.Count < count && await reader.ReadAsync(cancellationToken))
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
                await using var deleteCmd = CreateCmd($"DELETE FROM {_queueTableName} WHERE id = :id");
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

    // FOR UPDATE SKIP LOCKED locks rows as the client fetches them, and ODP.NET prefetches by
    // bytes. Size the prefetch to the batch so a pop does not lock (and hide from other pollers)
    // many more rows than it is going to take.
    private static void limitPrefetch(System.Data.Common.DbDataReader reader, int count)
    {
        if (reader is OracleDataReader oracleReader && oracleReader.RowSize > 0)
        {
            oracleReader.FetchSize = oracleReader.RowSize * Math.Max(count, 1);
        }
    }

    public async Task DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        try
        {
            await using var cmd1 = conn.CreateCommand(
                $"DELETE FROM {_queueTableName} WHERE {DatabaseConstants.KeepUntil} IS NOT NULL AND {DatabaseConstants.KeepUntil} <= SYSTIMESTAMP AT TIME ZONE 'UTC'");
            await cmd1.ExecuteNonQueryAsync(cancellationToken);

            await using var cmd2 = conn.CreateCommand(
                $"DELETE FROM {_scheduledTableName} WHERE {DatabaseConstants.KeepUntil} IS NOT NULL AND {DatabaseConstants.KeepUntil} <= SYSTIMESTAMP AT TIME ZONE 'UTC'");
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
