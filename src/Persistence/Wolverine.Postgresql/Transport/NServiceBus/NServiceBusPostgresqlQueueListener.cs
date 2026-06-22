using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Npgsql;
using Weasel.Core;
using Weasel.Postgresql;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Postgresql.Transport.NServiceBus;

internal class NServiceBusPostgresqlQueueListener : IListener
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly NServiceBusPostgresqlQueue _queue;
    private readonly IReceiver _receiver;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger _logger;
    private readonly NServiceBusPostgresqlEnvelopeMapper _mapper;
    private readonly NServiceBusPostgresqlQueueSender _sender;
    private readonly TimeSpan _pollingInterval;
    private readonly string _receiveSql;
    private Task? _task;

    public NServiceBusPostgresqlQueueListener(NServiceBusPostgresqlQueue queue, IWolverineRuntime runtime, IReceiver receiver)
    {
        _queue = queue;
        _receiver = receiver;
        _dataSource = queue.Parent.Store.NpgsqlDataSource;
        _logger = runtime.LoggerFactory.CreateLogger<NServiceBusPostgresqlQueueListener>();
        _mapper = queue.BuildMapper(runtime);
        _sender = new NServiceBusPostgresqlQueueSender(queue, runtime);
        _pollingInterval = queue.PollingInterval ?? runtime.DurabilitySettings.ScheduledJobPollingTime;

        Address = queue.Uri;

        // Destructive competing-consumer pop honoring NServiceBus FIFO-by-Seq ordering. The
        // column identifiers are left unquoted: Weasel provisions the queue table with
        // case-folded (lowercase) column names, so unquoted references resolve correctly.
        _receiveSql = $@"
WITH message AS (
    DELETE FROM {queue.TableIdentifier}
    WHERE ctid IN (
        SELECT ctid FROM {queue.TableIdentifier}
        ORDER BY Seq
        LIMIT :count
        FOR UPDATE SKIP LOCKED)
    RETURNING Id, Headers, Body)
SELECT Id, Headers, Body FROM message;";
    }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    public Uri Address { get; }

    public ValueTask CompleteAsync(Envelope envelope)
    {
        // The row was already removed by the destructive pop.
        return ValueTask.CompletedTask;
    }

    public async ValueTask DeferAsync(Envelope envelope)
    {
        // Re-expose the message by writing it back to the NServiceBus queue table.
        await _sender.SendAsync(envelope);
    }

    public ValueTask DisposeAsync()
    {
        return StopAsync();
    }

    public async ValueTask StopAsync()
    {
        await _cancellation.CancelAsync();
        _task?.SafeDispose();
    }

    public Task StartAsync()
    {
        _task = Task.Run(listenForMessagesAsync, _cancellation.Token);
        return Task.CompletedTask;
    }

    private async Task listenForMessagesAsync()
    {
        var failedCount = 0;

        while (!_cancellation.Token.IsCancellationRequested)
        {
            try
            {
                var messages = await tryPopAsync(_queue.MaximumMessagesToReceive, _cancellation.Token);
                failedCount = 0;

                if (messages.Count > 0)
                {
                    await _receiver.ReceivedAsync(this, messages.ToArray());
                }
                else
                {
                    await Task.Delay(_pollingInterval, _cancellation.Token);
                }
            }
            catch (Exception e)
            {
                if (e is TaskCanceledException or OperationCanceledException && _cancellation.IsCancellationRequested)
                {
                    break;
                }

                failedCount++;
                var pauseTime = failedCount > 5 ? 1.Seconds() : (failedCount * 100).Milliseconds();
                _logger.LogError(e, "Error while trying to receive messages from NServiceBus PostgreSQL queue {Name}",
                    _queue.Name);
                await Task.Delay(pauseTime);
            }
        }
    }

    private async Task<IReadOnlyList<Envelope>> tryPopAsync(int count, CancellationToken token)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(token);

        try
        {
            return await conn.CreateCommand(_receiveSql)
                .With("count", count)
                .FetchListAsync(async reader =>
                {
                    var id = await reader.GetFieldValueAsync<Guid>(0, token);
                    var headers = await reader.IsDBNullAsync(1, token)
                        ? null
                        : await reader.GetFieldValueAsync<string>(1, token);
                    var body = await reader.IsDBNullAsync(2, token)
                        ? Array.Empty<byte>()
                        : await reader.GetFieldValueAsync<byte[]>(2, token);

                    return _mapper.MapIncoming(id, headers, body);
                }, cancellation: token);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
