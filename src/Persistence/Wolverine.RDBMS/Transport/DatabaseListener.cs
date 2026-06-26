using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.RDBMS.Transport;

/// <summary>
/// Base class for the database-backed interop transport listeners (NServiceBus / MassTransit over
/// SQL Server or PostgreSQL). Owns the common polling loop: a cancellable background task that
/// repeatedly pops a batch of messages from a queue table/function, forwards them to the receiver,
/// and backs off on idle or error. Subclasses supply only the dialect-specific batch pop
/// (<see cref="TryPopAsync"/>) and the ack / nack behaviour (<see cref="CompleteAsync"/> /
/// <see cref="DeferAsync"/>).
/// </summary>
public abstract class DatabaseListener : IListener
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly IReceiver _receiver;
    private readonly ILogger _logger;
    private readonly TimeSpan _pollingInterval;
    private readonly string _queueName;
    private Task? _task;

    protected DatabaseListener(Uri address, string queueName, IReceiver receiver, ILogger logger,
        TimeSpan pollingInterval)
    {
        Address = address;
        _queueName = queueName;
        _receiver = receiver;
        _logger = logger;
        _pollingInterval = pollingInterval;
    }

    public Uri Address { get; }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    /// <summary>The cancellation token signalled when the listener is stopped.</summary>
    protected CancellationToken Cancellation => _cancellation.Token;

    /// <summary>The maximum number of messages to fetch in a single poll.</summary>
    protected abstract int MaximumMessagesToReceive { get; }

    /// <summary>Pop up to <paramref name="maximumMessages"/> messages from the database queue.</summary>
    protected abstract Task<IReadOnlyList<Envelope>> TryPopAsync(int maximumMessages, CancellationToken token);

    public abstract ValueTask CompleteAsync(Envelope envelope);

    public abstract ValueTask DeferAsync(Envelope envelope);

    public Task StartAsync()
    {
        _task = Task.Run(listenForMessagesAsync, _cancellation.Token);
        return Task.CompletedTask;
    }

    public async ValueTask StopAsync()
    {
        await _cancellation.CancelAsync();
        _task?.SafeDispose();
    }

    public ValueTask DisposeAsync()
    {
        return StopAsync();
    }

    private async Task listenForMessagesAsync()
    {
        var failedCount = 0;

        while (!_cancellation.Token.IsCancellationRequested)
        {
            try
            {
                var messages = await TryPopAsync(MaximumMessagesToReceive, _cancellation.Token);
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
                _logger.LogError(e, "Error while trying to receive messages from database queue {Name}", _queueName);
                await Task.Delay(pauseTime);
            }
        }
    }
}
