using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.SqlServer.Transport;

internal class QueueListener : IListener
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SqlServerQueue _queue;
    private readonly IReceiver _receiver;
    private readonly ILogger<QueueListener> _logger;
    private readonly Task _task;
    private readonly DurabilitySettings _settings;

    public QueueListener(SqlServerQueue queue, IWolverineRuntime runtime, IReceiver receiver)
    {
        _queue = queue;
        _receiver = receiver;
        _logger = runtime.LoggerFactory.CreateLogger<QueueListener>();
        _settings = runtime.DurabilitySettings;


        _task = Task.Run(listenForMessages, _cancellation.Token);
    }

    public ValueTask CompleteAsync(Envelope envelope)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        // TODO -- going to have to track attempts!
        // TODO -- stick back in the queue all over again
        // Use a retry block for this
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        _task.SafeDispose();
        return ValueTask.CompletedTask;
    }

    public Uri Address => _queue.Uri;
    public ValueTask StopAsync()
    {
        _cancellation.Cancel();
        
        return ValueTask.CompletedTask;
    }
    
    private async Task listenForMessages()
    {
        var failedCount = 0;

        while (!_cancellation.Token.IsCancellationRequested)
        {
            try
            {
                // TODO -- embed the MaximumMessagesToReceive internally to queue?
                // Or really make it back pressure aware.
                var messages = _queue.Mode == EndpointMode.Durable
                    ? await _queue.TryPopDurablyAsync(_queue.MaximumMessagesToReceive, _settings,
                        _cancellation.Token)
                    : await _queue.TryPopAsync(_queue.MaximumMessagesToReceive, _cancellation.Token);

                failedCount = 0;
                        
                // TODO -- maybe make this do some back pressure!
                if (messages.Any())
                {
                    // TODO -- do something right here to mark them as "already in inbox" or something
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