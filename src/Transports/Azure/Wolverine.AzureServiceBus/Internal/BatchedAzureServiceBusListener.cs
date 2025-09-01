using Azure.Messaging.ServiceBus;
using JasperFx.Blocks;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AzureServiceBus.Internal;

public class BatchedAzureServiceBusListener : IListener, ISupportDeadLetterQueue
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly RetryBlock<AzureServiceBusEnvelope> _complete;
    private readonly RetryBlock<AzureServiceBusEnvelope> _deadLetter;
    private readonly RetryBlock<Envelope> _defer;
    private readonly AzureServiceBusEndpoint _endpoint;
    private readonly ILogger _logger;
    private readonly IIncomingMapper<ServiceBusReceivedMessage> _mapper;
    private readonly ServiceBusReceiver _receiver;
    private readonly ISender _requeue;
    private readonly Task _task;
    private readonly IReceiver _wolverineReceiver;

    public BatchedAzureServiceBusListener(AzureServiceBusEndpoint endpoint, ILogger logger,
        IReceiver wolverineReceiver, ServiceBusReceiver receiver, IIncomingMapper<ServiceBusReceivedMessage> mapper,
        ISender requeue)
    {
        _endpoint = endpoint;
        _logger = logger;
        _wolverineReceiver = wolverineReceiver;
        _receiver = receiver;
        _mapper = mapper;
        _requeue = requeue;

        _task = Task.Run(listenForMessages, _cancellation.Token);

        _complete = new RetryBlock<AzureServiceBusEnvelope>((e, _) => { return e.CompleteAsync(_cancellation.Token); },
            _logger, _cancellation.Token);

        _defer = new RetryBlock<Envelope>(async (envelope, _) => { await _requeue.SendAsync(envelope); }, logger,
            _cancellation.Token);

        _deadLetter =
            new RetryBlock<AzureServiceBusEnvelope>(
                (e, c) => e.DeadLetterAsync(_cancellation.Token, e.Exception?.GetType().NameInCode(),
                    e.Exception?.Message), logger,
                _cancellation.Token);
    }

    public IHandlerPipeline? Pipeline => _wolverineReceiver.Pipeline;

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
        await _defer.PostAsync(envelope);
    }

    public async Task<bool> TryRequeueAsync(Envelope envelope)
    {
        if (envelope is AzureServiceBusEnvelope)
        {
            await _defer.PostAsync(envelope);
            return true;
        }

        return false;
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

    public async Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        if (envelope is AzureServiceBusEnvelope e)
        {
            e.Exception = exception;
            await _deadLetter.PostAsync(e);
        }
    }

    public bool NativeDeadLetterQueueEnabled => true;

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
                    var envelopes = new List<Envelope>(messages.Count);
                    foreach (var message in messages)
                    {
                        try
                        {
                            var envelope = new AzureServiceBusEnvelope(message, _receiver);
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

                if (_endpoint.Role == EndpointRole.Application)
                {
                    _logger.LogError(e, "Error while trying to retrieve messages from Azure Service Bus {Uri}",
                        _endpoint.Uri);
                }
                else
                {
                    _logger.LogError(e,
                        "Error while trying to retrieve messages from Azure Service Bus {Uri}. Check if system queues should be enabled for this application because this could be from the application being unable to create the system queues for Azure Service Bus",
                        _endpoint.Uri);
                }

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
}