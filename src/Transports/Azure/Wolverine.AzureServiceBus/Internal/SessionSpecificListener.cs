using Azure.Messaging.ServiceBus;
using JasperFx.Blocks;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util.Dataflow;

namespace Wolverine.AzureServiceBus.Internal;


internal class AzureServiceBusSessionListener : IListener
{
    private readonly AzureServiceBusTransport _transport;
    private readonly AzureServiceBusEndpoint _endpoint;
    private readonly IReceiver _receiver;
    private readonly IEnvelopeMapper<ServiceBusReceivedMessage, ServiceBusMessage> _mapper;
    private readonly ILogger _logger;
    private readonly ISender _requeue;
    private readonly CancellationTokenSource _cancellation = new();

    private readonly List<Task> _tasks = new();
    
    public AzureServiceBusSessionListener(AzureServiceBusTransport transport, AzureServiceBusEndpoint endpoint,
        IReceiver receiver, IEnvelopeMapper<ServiceBusReceivedMessage, ServiceBusMessage> mapper, ILogger logger,
        ISender requeue)
    {
        _transport = transport;
        _endpoint = endpoint;
        _receiver = receiver;
        _mapper = mapper;
        _logger = logger;
        _requeue = requeue;

        var listenerCount = _endpoint.ListenerCount;
        if (listenerCount == 0) listenerCount = 1;

        for (int i = 0; i < listenerCount; i++)
        {
            var task = Task.Run(listenForMessages, _cancellation.Token);
            _tasks.Add(task);
        }
    }

    private async Task listenForMessages()
    {
        var failedCount = 0;

        while (!_cancellation.Token.IsCancellationRequested)
        {
            try
            {
                await using var sessionReceiver = await _transport.AcceptNextSessionAsync(_endpoint, _cancellation.Token);
                var sessionListener =
                    new SessionSpecificListener(sessionReceiver, _endpoint, _receiver, _mapper, _logger, _requeue);
                var count = await sessionListener.ExecuteAsync(_cancellation.Token);

                failedCount = 0;

                if (count == 0)
                {
                    // TODO -- make this configurable
                    await Task.Delay(250.Milliseconds());
                }
            }
            catch (ServiceBusException sbe) when (sbe.Reason == ServiceBusFailureReason.ServiceTimeout)
            {
                // There's nothing there, so just slow down
                failedCount++;
                var pauseTime = failedCount > 5 ? 1.Seconds() : (failedCount * 100).Milliseconds();

                await Task.Delay(pauseTime);
            }
            catch (TaskCanceledException) when (_cancellation.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                failedCount++;
                var pauseTime = failedCount > 5 ? 1.Seconds() : (failedCount * 100).Milliseconds();

                if (_endpoint.Role == EndpointRole.Application)
                {
                    _logger.LogError(e, "Error while trying to retrieve messages from Azure Service Bus {Uri}",
                        _endpoint.Uri);
                }
                else
                {
                    _logger.LogError(e, "Error while trying to retrieve messages from Azure Service Bus {Uri}. Check if system queues should be enabled for this application because this could be from the application being unable to create the system queues for Azure Service Bus",
                        _endpoint.Uri);
                }

                await Task.Delay(pauseTime);
            }
        }
    }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    public ValueTask CompleteAsync(Envelope envelope)
    {
        throw new NotSupportedException();
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        throw new NotSupportedException();
    }

    public ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        foreach (var task in _tasks)
        {
            task.SafeDispose();
        }

        return ValueTask.CompletedTask;
    }

    public Uri Address => _endpoint.Uri;
    public ValueTask StopAsync()
    {
        _cancellation.Cancel();
        return ValueTask.CompletedTask;

    }
}

internal class SessionSpecificListener : IListener, ISupportDeadLetterQueue
{
    private readonly ServiceBusSessionReceiver _sessionReceiver;
    private readonly AzureServiceBusEndpoint _endpoint;
    private readonly IReceiver _receiver;
    private readonly IEnvelopeMapper<ServiceBusReceivedMessage, ServiceBusMessage> _mapper;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cancellation = new();

    private readonly RetryBlock<AzureServiceBusEnvelope> _complete;
    private readonly RetryBlock<AzureServiceBusEnvelope> _defer;
    private readonly RetryBlock<AzureServiceBusEnvelope> _deadLetter;

    public SessionSpecificListener(ServiceBusSessionReceiver sessionReceiver, AzureServiceBusEndpoint endpoint, IReceiver receiver, IEnvelopeMapper<ServiceBusReceivedMessage, ServiceBusMessage> mapper, ILogger logger,  ISender requeue)
    {
        _sessionReceiver = sessionReceiver;
        _endpoint = endpoint;
        _receiver = receiver;
        _mapper = mapper;
        _logger = logger;

        _complete = new RetryBlock<AzureServiceBusEnvelope>((e, _) => e.CompleteAsync(_cancellation.Token), _logger, _cancellation.Token);

        _defer = new RetryBlock<AzureServiceBusEnvelope>(async (envelope, _) =>
        {
            if (envelope is { } e)
            {
                await e.CompleteAsync(_cancellation.Token);
                e.IsCompleted = true;
            }

            await requeue.SendAsync(envelope);
        }, logger, _cancellation.Token);

        _deadLetter =
            new RetryBlock<AzureServiceBusEnvelope>((e, c) => e.DeadLetterAsync(_cancellation.Token, deadLetterReason:e.Exception?.GetType().NameInCode(), deadLetterErrorDescription:e.Exception?.Message), logger,
                _cancellation.Token);
    }

    public async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var messages =
            await _sessionReceiver.ReceiveMessagesAsync(_endpoint.MaximumMessagesToReceive, _endpoint.MaximumWaitTime, cancellationToken);

        foreach (var message in messages)
        {
            try
            {
                var envelope = new AzureServiceBusEnvelope(message, _sessionReceiver);

                _mapper.MapIncomingToEnvelope(envelope, message);

                try
                {
                    await _receiver.ReceivedAsync(this, envelope);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Failure to receive an incoming message with {Id}, trying to 'Defer' the message", envelope.Id);

                    try
                    {
                        await DeferAsync(envelope);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogError(exception, "Failure trying to Nack a previously failed message {Id}", envelope.Id);
                    }
                }
            }
            catch (Exception e)
            {
                await tryMoveToDeadLetterQueue(message);
                _logger.LogError(e, "Error while reading message {Id} from {Uri}", message.MessageId,
                    _endpoint.Uri);
            }
        }

        await _deadLetter.DrainAsync();
        await _complete.DrainAsync();
        await _defer.DrainAsync();

        return messages.Count;
    }

    private async Task tryMoveToDeadLetterQueue(ServiceBusReceivedMessage message)
    {
        try
        {
            await _sessionReceiver.DeadLetterMessageAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failure while trying to move message {Id} to the dead letter queue",
                message.MessageId);
        }
    }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    public ValueTask CompleteAsync(Envelope envelope)
    {
        if (envelope is AzureServiceBusEnvelope e)
        {
            var task = _complete.PostAsync(e);
            return new ValueTask(task);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        if (envelope is AzureServiceBusEnvelope e)
        {
            var task = _defer.PostAsync(e);
            return new ValueTask(task);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public Uri Address => _endpoint.Uri;
    public ValueTask StopAsync()
    {
        return ValueTask.CompletedTask;
    }

    public bool NativeDeadLetterQueueEnabled => true;
    public async Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        if (envelope is AzureServiceBusEnvelope e)
        {
            e.Exception = exception;
            await _deadLetter.PostAsync(e);
        }
    }
}