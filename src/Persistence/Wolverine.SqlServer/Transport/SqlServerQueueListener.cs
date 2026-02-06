using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Weasel.SqlServer;
using Wolverine.Configuration;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.SqlServer.Transport;

internal class SqlServerQueueListener : IListener
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SqlServerQueue _queue;
    private readonly IReceiver _receiver;
    private readonly string _connectionString;
    private readonly string? _databaseName;
    private readonly ILogger<SqlServerQueueListener> _logger;
    private Task? _task;
    private readonly DurabilitySettings _settings;
    private Task? _scheduledTask;
    private readonly SqlServerQueueSender _sender;
    private readonly string _tryPopMessagesDirectlySql;
    private readonly string _tryPopMessagesToInboxSql;
    private readonly string _moveScheduledToReadyQueueSql;
    private readonly string _deleteExpiredSql;

    public SqlServerQueueListener(SqlServerQueue queue, IWolverineRuntime runtime, IReceiver receiver)
        : this(queue, runtime, receiver, queue.Parent.Settings.ConnectionString, null)
    {
    }

    public SqlServerQueueListener(SqlServerQueue queue, IWolverineRuntime runtime, IReceiver receiver,
        string connectionString, string? databaseName)
    {
        Address = SqlServerQueue.ToUri(queue.Name, databaseName);
        _queue = queue;
        _receiver = receiver;
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _databaseName = databaseName;
        _logger = runtime.LoggerFactory.CreateLogger<SqlServerQueueListener>();
        _settings = runtime.DurabilitySettings;

        _sender = new SqlServerQueueSender(queue, connectionString, databaseName);

        var queueTableIdentifier = queue.QueueTable.Identifier;
        var scheduledTableIdentifier = queue.ScheduledTable.Identifier;

        _tryPopMessagesDirectlySql = $@"
DECLARE @NOCOUNT VARCHAR(3) = 'OFF';
IF ( (512 & @@OPTIONS) = 512 ) SET @NOCOUNT = 'ON';
SET NOCOUNT ON;

WITH message AS (
    SELECT TOP(@count) {DatabaseConstants.Body}, {DatabaseConstants.KeepUntil}
    FROM {queueTableIdentifier} WITH (UPDLOCK, READPAST, ROWLOCK)
    ORDER BY {queueTableIdentifier}.timestamp)
DELETE FROM message
OUTPUT
    deleted.{DatabaseConstants.Body};

IF (@NOCOUNT = 'ON') SET NOCOUNT ON;
IF (@NOCOUNT = 'OFF') SET NOCOUNT OFF;";

        _tryPopMessagesToInboxSql = $@"
DECLARE @NOCOUNT VARCHAR(3) = 'OFF';
IF ( (512 & @@OPTIONS) = 512 ) SET @NOCOUNT = 'ON';
SET NOCOUNT ON;

delete FROM {queueTableIdentifier} WITH (UPDLOCK, READPAST, ROWLOCK) where id in (select id from {queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.IncomingTable});
select top(@count) id, body, message_type, keep_until into #temp_pop_{queue.Name}
FROM {queueTableIdentifier} WITH (UPDLOCK, READPAST, ROWLOCK)
ORDER BY {queueTableIdentifier}.timestamp;
delete from {queueTableIdentifier} where id in (select id from #temp_pop_{queue.Name});
INSERT INTO {queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.IncomingTable}
(id, status, owner_id, body, message_type, received_at, keep_until)
 SELECT id, 'Incoming', @node, body, message_type, '{Address}', keep_until FROM #temp_pop_{queue.Name};
select body from #temp_pop_{queue.Name};

IF (@NOCOUNT = 'ON') SET NOCOUNT ON;
IF (@NOCOUNT = 'OFF') SET NOCOUNT OFF;";

        _moveScheduledToReadyQueueSql = $@"
select id, body, message_type, keep_until into #temp_move_{queue.Name}
FROM {scheduledTableIdentifier} WITH (UPDLOCK, READPAST, ROWLOCK)
WHERE {DatabaseConstants.ExecutionTime} <= SYSDATETIMEOFFSET() AND ID NOT IN (select id from {queueTableIdentifier})
ORDER BY {scheduledTableIdentifier}.timestamp;
delete from {scheduledTableIdentifier} where id in (select id from #temp_move_{queue.Name});
INSERT INTO {queueTableIdentifier}
(id, body, message_type, keep_until)
 SELECT id, body, message_type, keep_until FROM #temp_move_{queue.Name};
select count(*) from #temp_move_{queue.Name}
";

        _deleteExpiredSql =
            $"delete from {queueTableIdentifier} where {DatabaseConstants.KeepUntil} IS NOT NULL and {DatabaseConstants.KeepUntil} <= SYSDATETIMEOFFSET();delete from {scheduledTableIdentifier} where {DatabaseConstants.KeepUntil} IS NOT NULL and {DatabaseConstants.KeepUntil} <= SYSDATETIMEOFFSET()";
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
        _task?.SafeDispose();
        _scheduledTask?.SafeDispose();
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

    public async Task StartAsync()
    {
        if (_queue.Parent.AutoProvision)
        {
            await _queue.EnsureSchemaExists(_databaseName ?? string.Empty, _connectionString);
        }

        _task = Task.Run(listenForMessagesAsync, _cancellation.Token);
        _scheduledTask = Task.Run(lookForScheduledMessagesAsync, _cancellation.Token);
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
                    _logger.LogInformation(
                        "Propagated {Number} scheduled messages to Sql Server-backed queue {Queue}", count,
                        _queue.Name);
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

                _logger.LogError(e, "Error while trying to propagate scheduled messages from Sql Server Queue {Name}",
                    _queue.Name);

                await Task.Delay(pauseTime);
            }
        }
    }

    public async Task<int> MoveScheduledToReadyQueueAsync(CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var count = (int)await conn.CreateCommand(_moveScheduledToReadyQueueSql)
            .ExecuteScalarAsync(cancellationToken);

        await conn.CloseAsync();

        return count;
    }

    public async Task DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await conn.CreateCommand(_deleteExpiredSql).ExecuteNonQueryAsync(cancellationToken);
        await conn.CloseAsync();
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

                _logger.LogError(e, "Error while trying to retrieve messages from Sql Server Queue {Name}",
                    _queue.Name);

                await Task.Delay(pauseTime);
            }
        }
    }

    public async Task<IReadOnlyList<Envelope>> TryPopAsync(int count, ILogger logger,
        CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

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
                    logger.LogError(e,
                        "Error trying to deserialize Envelope data in Sql Transport Queue {Queue}, discarding",
                        _queue.Name);
                    return Envelope.ForPing(Address); // just a stand in
                }
            }, cancellationToken);
    }

    public async Task<IReadOnlyList<Envelope>> TryPopDurablyAsync(int count, DurabilitySettings settings,
        ILogger logger, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        return await conn
            .CreateCommand(_tryPopMessagesToInboxSql)
            .With("count", count)
            .With("node", settings.AssignedNodeNumber)
            .FetchListAsync<Envelope>(async reader =>
            {
                var data = await reader.GetFieldValueAsync<byte[]>(0, cancellationToken);
                try
                {
                    return EnvelopeSerializer.Deserialize(data);
                }
                catch (Exception e)
                {
                    logger.LogError(e,
                        "Error trying to deserialize Envelope data in Sql Transport Queue {Queue}, discarding",
                        _queue.Name);
                    return Envelope.ForPing(Address); // just a stand in
                }
            }, cancellationToken);
    }
}
