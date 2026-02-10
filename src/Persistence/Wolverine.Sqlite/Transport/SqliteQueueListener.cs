using System.Data.Common;
using JasperFx.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.Configuration;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.Sqlite.Transport;

internal class SqliteQueueListener : IListener
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SqliteQueue _queue;
    private readonly IReceiver _receiver;
    private readonly DbDataSource _dataSource;
    private readonly string? _databaseName;
    private readonly ILogger<SqliteQueueListener> _logger;
    private Task? _task;
    private readonly DurabilitySettings _settings;
    private Task? _scheduledTask;
    private readonly SqliteQueueSender _sender;
    private readonly string _queueTableName;
    private readonly string _queueName;
    private readonly string _scheduledTableName;

    public SqliteQueueListener(SqliteQueue queue, IWolverineRuntime runtime, IReceiver receiver,
        DbDataSource dataSource, string? databaseName)
    {
        Address = SqliteQueue.ToUri(queue.Name, databaseName);
        _queue = queue;
        _receiver = receiver;
        _dataSource = dataSource;
        _databaseName = databaseName;
        _logger = runtime.LoggerFactory.CreateLogger<SqliteQueueListener>();
        _settings = runtime.DurabilitySettings;

        _sender = new SqliteQueueSender(queue, _dataSource, databaseName);

        _queueTableName = _queue.QueueTable.Identifier.QualifiedName;
        _scheduledTableName = _queue.ScheduledTable.Identifier.QualifiedName;

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
                    _logger.LogInformation(
                        "Propagated {Number} scheduled messages to SQLite-backed queue {Queue}", count, _queueName);
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

                _logger.LogError(e, "Error while trying to propagate scheduled messages from SQLite Queue {Name}",
                    _queueName);

                await Task.Delay(pauseTime);
            }
        }
    }

    public async Task<long> MoveScheduledToReadyQueueAsync(CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        long count = 0;

        try
        {
            // SQLite doesn't support complex CTEs like PostgreSQL, so we use a simpler approach
            var tx = await conn.BeginTransactionAsync(cancellationToken);

            // Move scheduled messages that are ready
            var moveCommand = conn.CreateCommand();
            moveCommand.Transaction = tx;
            moveCommand.CommandText = $@"
                INSERT INTO {_queueTableName} (id, body, message_type)
                SELECT id, body, message_type
                FROM {_scheduledTableName}
                WHERE execution_time <= datetime('now')
                AND id NOT IN (SELECT id FROM {_queueTableName})
            ";

            await moveCommand.ExecuteNonQueryAsync(cancellationToken);

            // Delete moved messages from scheduled table
            var deleteCommand = conn.CreateCommand();
            deleteCommand.Transaction = tx;
            deleteCommand.CommandText = $@"
                DELETE FROM {_scheduledTableName}
                WHERE execution_time <= datetime('now')
            ";

            count = await deleteCommand.ExecuteNonQueryAsync(cancellationToken);

            await tx.CommitAsync(cancellationToken);
        }
        finally
        {
            await conn.CloseAsync();
        }

        return count;
    }

    public async Task DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await conn.CreateCommand($"delete from {_queueTableName} where keep_until is not null and keep_until < datetime('now')")
            .ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task StartAsync()
    {
        _task = Task.Run(tryPopMessages, _cancellation.Token);
        _scheduledTask = Task.Run(lookForScheduledMessagesAsync, _cancellation.Token);

        await Task.CompletedTask;
    }

    private async Task tryPopMessages()
    {
        await Task.Delay(_settings.FirstNodeReassignmentExecution);

        var failedCount = 0;

        while (!_cancellation.Token.IsCancellationRequested)
        {
            try
            {
                await using var conn = await _dataSource.OpenConnectionAsync(_cancellation.Token).ConfigureAwait(false);

                try
                {
                    var tx = await conn.BeginTransactionAsync(_cancellation.Token);

                    var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = $@"
                        SELECT {DatabaseConstants.Body}
                        FROM {_queueTableName}
                        ORDER BY timestamp
                        LIMIT {_queue.MaximumMessagesToReceive}
                    ";

                    var envelopes = new List<Envelope>();

                    await using (var reader = await cmd.ExecuteReaderAsync(_cancellation.Token))
                    {
                        while (await reader.ReadAsync(_cancellation.Token))
                        {
                            var body = (byte[])reader.GetValue(0);
                            var envelope = EnvelopeSerializer.Deserialize(body);
                            envelopes.Add(envelope!);
                        }
                    }

                    if (envelopes.Any())
                    {
                        // Delete the messages we retrieved
                        var ids = string.Join(",", envelopes.Select(e => $"'{e.Id.ToString().ToUpperInvariant()}'"));
                        var deleteCmd = conn.CreateCommand();
                        deleteCmd.Transaction = tx;
                        deleteCmd.CommandText = $"DELETE FROM {_queueTableName} WHERE {DatabaseConstants.Id} IN ({ids})";
                        await deleteCmd.ExecuteNonQueryAsync(_cancellation.Token);

                        await tx.CommitAsync(_cancellation.Token);

                        foreach (var envelope in envelopes)
                        {
                            envelope.Destination = Address;
                            envelope.ReplyUri = Address;

                            await _receiver.ReceivedAsync(this, envelope);
                        }
                    }
                    else
                    {
                        await tx.RollbackAsync(_cancellation.Token);
                        await Task.Delay(250.Milliseconds(), _cancellation.Token);
                    }
                }
                finally
                {
                    await conn.CloseAsync();
                }

                failedCount = 0;
            }
            catch (Exception e)
            {
                if (e is TaskCanceledException && _cancellation.IsCancellationRequested)
                {
                    break;
                }

                failedCount++;
                var pauseTime = failedCount > 5 ? 1.Seconds() : (failedCount * 250).Milliseconds();

                _logger.LogError(e, "Error trying to pop messages from SQLite queue {Name}", _queueName);

                await Task.Delay(pauseTime);
            }
        }
    }
}
