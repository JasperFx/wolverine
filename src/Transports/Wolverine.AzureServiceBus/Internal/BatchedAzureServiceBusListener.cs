using Azure.Messaging.ServiceBus;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util.Dataflow;

namespace Wolverine.AzureServiceBus.Internal;

public class BatchedAzureServiceBusListener : IListener, ISupportDeadLetterQueue
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly RetryBlock<AzureServiceBusEnvelope> _complete;
    private readonly RetryBlock<AzureServiceBusEnvelope> _defer;
    private readonly AzureServiceBusEndpoint _endpoint;
    private readonly ILogger _logger;
    private readonly IIncomingMapper<ServiceBusReceivedMessage> _mapper;
    private readonly ISender _requeue;
    private readonly ServiceBusReceiver _receiver;
    private readonly Task _task;
    private readonly IReceiver _wolverineReceiver;
    private readonly RetryBlock<AzureServiceBusEnvelope> _deadLetter;

    public BatchedAzureServiceBusListener(AzureServiceBusEndpoint endpoint, ILogger logger,
        IReceiver wolverineReceiver, ServiceBusReceiver receiver, IIncomingMapper<ServiceBusReceivedMessage> mapper, ISender requeue)
    {
        _endpoint = endpoint;
        _logger = logger;
        _wolverineReceiver = wolverineReceiver;
        _receiver = receiver;
        _mapper = mapper;
        _requeue = requeue;

        _task = Task.Run(listenForMessages, _cancellation.Token);

        _complete = new RetryBlock<AzureServiceBusEnvelope>((e, _) =>
        {
            return _receiver.CompleteMessageAsync(e.AzureMessage);
        }, _logger, _cancellation.Token);

        _defer = new RetryBlock<AzureServiceBusEnvelope>((e, _) =>
        {
            return _receiver.DeferMessageAsync(e.AzureMessage,
                new Dictionary<string, object> { { EnvelopeConstants.AttemptsKey, e.Attempts + 1 } });
        }, logger, _cancellation.Token);

        _deadLetter =
            new RetryBlock<AzureServiceBusEnvelope>((e, c) => _receiver.DeadLetterMessageAsync(e.AzureMessage, deadLetterReason:e.Exception?.GetType().NameInCode(), deadLetterErrorDescription:e.Exception?.Message), logger,
                _cancellation.Token);
    }

    public ValueTask CompleteAsync(Envelope envelope)
    {
        if (envelope is AzureServiceBusEnvelope e)
        {
            var task = _complete.PostAsync(e);
            return new ValueTask(task);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DeferAsync(Envelope envelope)
    {
        if (envelope is AzureServiceBusEnvelope e)
        {
            await _receiver.CompleteMessageAsync(e.AzureMessage);
            e.IsCompleted = true;
        }
        
        await _requeue.SendAsync(envelope);
    }

    public ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        _task.SafeDispose();
        _complete.SafeDispose();
        _defer.SafeDispose();
        _deadLetter.SafeDispose();
        return _receiver.DisposeAsync();
    }

    public Uri Address => _endpoint.Uri;

    public ValueTask StopAsync()
    {
        _cancellation.Cancel();
        return new ValueTask(_receiver.CloseAsync());
    }

    private async Task listenForMessages()
    {
        var failedCount = 0;

        while (!_cancellation.Token.IsCancellationRequested)
        {
            try
            {
                var messages = await _receiver
                    .ReceiveMessagesAsync(_endpoint.MaximumMessagesToReceive,
                        _endpoint.MaximumWaitTime, _cancellation.Token);

                failedCount = 0;

                if (messages.Any())
                {
                    var envelopes = new List<Envelope>();
                    foreach (var message in messages)
                    {
                        try
                        {
                            var envelope = new AzureServiceBusEnvelope(message);
                            _mapper.MapIncomingToEnvelope(envelope, message);

                            envelopes.Add(envelope);
                        }
                        catch (Exception e)
                        {
                            await tryMoveToDeadLetterQueue(message);
                            _logger.LogError(e, "Error while reading message {Id} from {Uri}", message.MessageId,
                                _endpoint.Uri);
                        }
                    }

                    if (envelopes.Any())
                    {
                        await _wolverineReceiver.ReceivedAsync(this, envelopes.ToArray());
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
                if (e is TaskCanceledException && _cancellation.IsCancellationRequested)
                {
                    break;
                }
                
                failedCount++;
                var pauseTime = failedCount > 5 ? 1.Seconds() : (failedCount * 100).Milliseconds();

                _logger.LogError(e, "Error while trying to retrieve messages from Azure Service Bus {Uri}",
                    _endpoint.Uri);
                await Task.Delay(pauseTime);
            }
        }
    }

    private async Task tryMoveToDeadLetterQueue(ServiceBusReceivedMessage message)
    {
        try
        {
            await _receiver.DeadLetterMessageAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failure while trying to move message {Id} to the dead letter queue",
                message.MessageId);
        }
    }

    public async Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        if (envelope is AzureServiceBusEnvelope e)
        {
            e.Exception = exception;
            await _deadLetter.PostAsync(e);
        }
    }

    public bool NativeDeadLetterQueueEnabled => true;
}