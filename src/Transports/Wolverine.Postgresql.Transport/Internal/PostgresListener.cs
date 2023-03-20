using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Util.Dataflow;

namespace Wolverine.Transports.Postgresql.Internal;

internal sealed class PostgresListener : IListener
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly RetryBlock<PostgresEnvelope> _complete;
    private readonly RetryBlock<PostgresEnvelope> _defer;
    private readonly PostgresEndpoint _endpoint;
    private readonly ILogger _logger;
    private readonly IIncomingMapper<PostgresMessage> _mapper;
    private readonly Task _task;
    private readonly IReceiver _wolverineReceiver;
    private readonly PostgresQueueListener _queueListener;

    public PostgresListener(
        PostgresEndpoint endpoint,
        ILogger logger,
        IReceiver wolverineReceiver,
        PostgresQueueListener queueListener,
        IIncomingMapper<PostgresMessage> mapper)
    {
        _endpoint = endpoint;
        _logger = logger;
        _wolverineReceiver = wolverineReceiver;
        _queueListener = queueListener;
        _mapper = mapper;

        _task = Task.Run(ListenForMessages, _cancellation.Token);

        _complete = new RetryBlock<PostgresEnvelope>(
            (e, ct) => _queueListener.CompleteAsync(e.PostgresMessage.Id, ct),
            _logger,
            _cancellation.Token);

        _defer = new RetryBlock<PostgresEnvelope>(
            (e, ct) => _queueListener.DeferAsync(e.PostgresMessage.Id, ct),
            _logger,
            _cancellation.Token);
    }

    public Uri Address => _endpoint.Uri;

    public ValueTask CompleteAsync(Envelope envelope)
    {
        if (envelope is PostgresEnvelope e)
        {
            var task = _complete.PostAsync(e);
            return new ValueTask(task);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        if (envelope is PostgresEnvelope e)
        {
            var task = _defer.PostAsync(e);
            return new ValueTask(task);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync()
    {
        _cancellation.Cancel();
        return default;
    }
    
    public ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        _task.SafeDispose();
        _complete.SafeDispose();
        _defer.SafeDispose();
        return _queueListener.DisposeAsync();
    }

    private async Task ListenForMessages()
    {
        var failedCount = 0;

        while (!_cancellation.Token.IsCancellationRequested)
        {
            try
            {
                var message = await _queueListener.ReadNext(_cancellation.Token);

                failedCount = 0;

                if (message is not null)
                {
                    try
                    {
                        var envelope = new PostgresEnvelope(message);

                        _mapper.MapIncomingToEnvelope(envelope, message);

                        await _wolverineReceiver.ReceivedAsync(this, envelope);
                    }
                    catch (Exception e)
                    {
                        await TryMoveToDeadLetterQueue(message);

                        _logger.LogError(e,
                            "Error while reading message {Id} from {Uri}",
                            message.Id,
                            _endpoint.Uri);
                    }
                }
                else
                {
                    // Slow down if this is a periodically used queue
                    await Task.Delay(250.Milliseconds());
                }
            }
            catch (Exception e)
            {
                failedCount++;
                var pauseTime = failedCount > 5 ? 1.Seconds() : (failedCount * 100).Milliseconds();

                _logger.LogError(e,
                    "Error while trying to read message from postgress {Uri}",
                    _endpoint.Uri);
                await Task.Delay(pauseTime);
            }
        }
    }

    private async Task TryMoveToDeadLetterQueue(PostgresMessage message)
    {
        // TODO implement dead letter queue
        throw new NotImplementedException();
        // try
        // {
        //     await _receiver.DeadLetterMessageAsync(message);
        // }
        // catch (Exception ex)
        // {
        //     _logger.LogError(ex,
        //         "Failure while trying to move message {Id} to the dead letter queue",
        //         message.MessageId);
        // }
    }
}