using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Weasel.SqlServer;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.SqlServer.Transport.NServiceBus;

internal class NServiceBusSqlServerQueueListener : IListener
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly NServiceBusSqlServerQueue _queue;
    private readonly IReceiver _receiver;
    private readonly string _connectionString;
    private readonly ILogger _logger;
    private readonly NServiceBusSqlServerEnvelopeMapper _mapper;
    private readonly NServiceBusSqlServerQueueSender _sender;
    private readonly TimeSpan _pollingInterval;
    private readonly string _receiveSql;
    private Task? _task;

    public NServiceBusSqlServerQueueListener(NServiceBusSqlServerQueue queue, IWolverineRuntime runtime, IReceiver receiver)
    {
        _queue = queue;
        _receiver = receiver;
        _connectionString = queue.Parent.Settings.ConnectionString!;
        _logger = runtime.LoggerFactory.CreateLogger<NServiceBusSqlServerQueueListener>();
        _mapper = queue.BuildMapper(runtime);
        _sender = new NServiceBusSqlServerQueueSender(queue, runtime);
        _pollingInterval = queue.PollingInterval ?? runtime.DurabilitySettings.ScheduledJobPollingTime;

        Address = queue.Uri;

        // Destructive competing-consumer pop honoring NServiceBus FIFO-by-RowVersion ordering.
        _receiveSql = $@"
WITH message AS (
    SELECT TOP(@count) Id, Headers, Body
    FROM {queue.TableIdentifier} WITH (UPDLOCK, READPAST, ROWLOCK)
    ORDER BY RowVersion)
DELETE FROM message
OUTPUT deleted.Id, deleted.Headers, deleted.Body;";
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
                _logger.LogError(e, "Error while trying to receive messages from NServiceBus Sql Server queue {Name}",
                    _queue.Name);
                await Task.Delay(pauseTime);
            }
        }
    }

    private async Task<IReadOnlyList<Envelope>> tryPopAsync(int count, CancellationToken token)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(token);

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
