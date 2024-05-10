using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.SqlServer.Transport;

internal class SqlServerQueueListener : IListener
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SqlServerQueue _queue;
    private readonly IReceiver _receiver;
    private readonly ILogger<SqlServerQueueListener> _logger;
    private readonly Task _task;
    private readonly DurabilitySettings _settings;
    private readonly Task _scheduledTask;

    public SqlServerQueueListener(SqlServerQueue queue, IWolverineRuntime runtime, IReceiver receiver)
    {
        _queue = queue;
        _receiver = receiver;
        _logger = runtime.LoggerFactory.CreateLogger<SqlServerQueueListener>();
        _settings = runtime.DurabilitySettings;

        _task = Task.Run(listenForMessagesAsync, _cancellation.Token);
        _scheduledTask = Task.Run(lookForScheduledMessagesAsync, _cancellation.Token);
    }

    public ValueTask CompleteAsync(Envelope envelope)
    {
        return ValueTask.CompletedTask;
    }

    public async ValueTask DeferAsync(Envelope envelope)
    {
        await _queue.SendAsync(envelope, _cancellation.Token);
    }

    public ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        _task.SafeDispose();
        _scheduledTask.SafeDispose();
        return ValueTask.CompletedTask;
    }

    public Uri Address => _queue.Uri;
    public ValueTask StopAsync()
    {
        _cancellation.Cancel();

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
                var count = await _queue.MoveScheduledToReadyQueueAsync(_cancellation.Token);
                if (count > 0)
                {
                    _logger.LogInformation("Propagated {Number} scheduled messages to Sql Server-backed queue {Queue}", count, _queue.Name);
                }

                await _queue.DeleteExpiredAsync(CancellationToken.None);

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

    private async Task listenForMessagesAsync()
    {
        var failedCount = 0;

        while (!_cancellation.Token.IsCancellationRequested)
        {
            try
            {

                var messages = _queue.Mode == EndpointMode.Durable
                    ? await _queue.TryPopDurablyAsync(_queue.MaximumMessagesToReceive, _settings, _logger,
                        _cancellation.Token)
                    : await _queue.TryPopAsync(_queue.MaximumMessagesToReceive, _logger, _cancellation.Token);

                failedCount = 0;

                if (messages.Any())
                {
                    await _receiver.ReceivedAsync(this, messages.ToArray());
                }
                else
                {
                    // Slow down if this is a periodically used queue
                    await Task.Delay(250.Milliseconds());
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
}