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

internal class AzureServiceBusSessionListener : IListener
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly AzureServiceBusEndpoint _endpoint;
    private readonly ILogger _logger;
    private readonly IEnvelopeMapper<ServiceBusReceivedMessage, ServiceBusMessage> _mapper;
    private readonly IReceiver _receiver;
    private readonly ISender _requeue;

    private readonly List<Task> _tasks = new();
    private readonly AzureServiceBusTransport _transport;
    private int _disposed;

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

        // GH-3494 (AO2): exactly ONE accept loop per listener instance. ListeningAgent already
        // builds Endpoint.ListenerCount of these listeners, so spawning ListenerCount loops inside
        // each one made RequireSessions(n) open n-squared concurrent AcceptNextSessionAsync loops --
        // 25 for RequireSessions(5) -- all competing for the same sessions.
        _tasks.Add(Task.Run(listenForMessages, _cancellation.Token));
    }

    /// <summary>
    /// The number of concurrent AcceptNextSessionAsync loops this listener runs. Exposed for testing.
    /// </summary>
    internal int AcceptLoopCount => _tasks.Count;

    /// <summary>
    /// The accept loop tasks this listener owns. Exposed for testing.
    /// </summary>
    internal IReadOnlyList<Task> AcceptLoops => _tasks;

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    public ValueTask CompleteAsync(Envelope envelope)
    {
        throw new NotSupportedException();
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        throw new NotSupportedException();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        // GH-5220: cancel AND join the accept loop before the CancellationTokenSource goes away. Disposing
        // it while the loop was still awaiting an accept or a back off left the loop to read the disposed
        // source on its next pass and fault with an unobserved ObjectDisposedException.
        await stopAcceptLoopsAsync();
        _cancellation.Dispose();
    }

    public Uri Address => _endpoint.Uri;

    public async ValueTask StopAsync()
    {
        if (Volatile.Read(ref _disposed) == 1) return;

        await stopAcceptLoopsAsync();
    }

    private async Task stopAcceptLoopsAsync()
    {
        try
        {
            await _cancellation.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // A concurrent DisposeAsync got there first, and it only disposes after joining the loop
            return;
        }

        foreach (var task in _tasks)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                // Expected while shutting down
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "Error while stopping the Azure Service Bus session listener at {Uri}",
                    _endpoint.Uri);
            }
        }
    }

    private async Task listenForMessages()
    {
        // GH-5220: capture the token once. _cancellation.Token throws once the source is disposed, while a
        // captured CancellationToken stays safe to inspect.
        var token = _cancellation.Token;
        var failedCount = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var sessionReceiver =
                    await _transport.AcceptNextSessionAsync(_endpoint, token);
                var sessionListener =
                    new SessionSpecificListener(sessionReceiver, _endpoint, _receiver, _mapper, _logger, _requeue);
                var count = await sessionListener.ExecuteAsync(token);

                failedCount = 0;

                if (count == 0)
                {
                    // TODO -- make this configurable
                    await backOffAsync(250.Milliseconds(), token);
                }
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                // Shutting down. Whatever the accept or receive threw on the way out -- a cancellation, or a
                // broker timeout that landed at the same moment -- the loop is done.
                break;
            }
            catch (ServiceBusException sbe) when (sbe.Reason == ServiceBusFailureReason.ServiceTimeout)
            {
                // There's nothing there, so just slow down
                failedCount++;
                var pauseTime = failedCount > 5 ? 1.Seconds() : (failedCount * 100).Milliseconds();

                await backOffAsync(pauseTime, token);
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
                    _logger.LogError(e,
                        "Error while trying to retrieve messages from Azure Service Bus {Uri}. Check if system queues should be enabled for this application because this could be from the application being unable to create the system queues for Azure Service Bus",
                        _endpoint.Uri);
                }

                await backOffAsync(pauseTime, token);
            }
        }
    }

    /// <summary>
    /// A back off that ends early, without throwing, when the listener shuts down. The loop guard then sees
    /// the cancellation and exits.
    /// </summary>
    private static async Task backOffAsync(TimeSpan pauseTime, CancellationToken token)
    {
        try
        {
            await Task.Delay(pauseTime, token);
        }
        catch (OperationCanceledException)
        {
            // Shutting down
        }
    }
}

internal class SessionSpecificListener : IListener, ISupportDeadLetterQueue
{
    private readonly CancellationTokenSource _cancellation = new();

    private readonly RetryBlock<AzureServiceBusEnvelope> _complete;
    private readonly RetryBlock<AzureServiceBusEnvelope> _deadLetter;
    private readonly RetryBlock<AzureServiceBusEnvelope> _defer;
    private readonly AzureServiceBusEndpoint _endpoint;
    private readonly ILogger _logger;
    private readonly IEnvelopeMapper<ServiceBusReceivedMessage, ServiceBusMessage> _mapper;
    private readonly IReceiver _receiver;
    private readonly ServiceBusSessionReceiver _sessionReceiver;

    public SessionSpecificListener(ServiceBusSessionReceiver sessionReceiver, AzureServiceBusEndpoint endpoint,
        IReceiver receiver, IEnvelopeMapper<ServiceBusReceivedMessage, ServiceBusMessage> mapper, ILogger logger,
        ISender requeue)
    {
        _sessionReceiver = sessionReceiver;
        _endpoint = endpoint;
        _receiver = receiver;
        _mapper = mapper;
        _logger = logger;

        // GH-4012 item 5. This settle had no terminal classification at all: item 3 landed it in
        // AzureServiceBusSettlement and routed the two non-session listeners through the helper, but both
        // session listeners call CompleteAsync directly and so kept burning the full retry budget on a
        // failure that can never succeed. Declaring the predicate ON THE BLOCK is what makes that hard to
        // forget again -- "remember to call the helper" is exactly what was forgotten here.
        _complete = AzureServiceBusSettlement.CompleteBlock((e, _) => e.CompleteAsync(_cancellation.Token),
            _logger, _cancellation.Token);

        _defer = new RetryBlock<AzureServiceBusEnvelope>(async (envelope, _) =>
        {
            if (envelope is { IsCompleted: false } e)
            {
                await e.CompleteAsync(_cancellation.Token);
            }

            await requeue.SendAsync(envelope);
        }, logger, _cancellation.Token);

        _deadLetter =
            new RetryBlock<AzureServiceBusEnvelope>(
                (e, c) => e.DeadLetterAsync(_cancellation.Token, e.Exception?.GetType().NameInCode(),
                    e.Exception?.Message), logger,
                _cancellation.Token);
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
            // GH-3474: the diagnostic headers have to be on the envelope BEFORE the move, because
            // AzureServiceBusEnvelope.buildDiagnosticProperties reads them off Headers to carry them onto the
            // dead lettered message. This listener was the only one of the four that never stamped them, so a
            // message dead lettered from a session-specific listener reached $DeadLetterQueue with none of
            // the failure diagnostics its three siblings attach.
            DeadLetterQueueConstants.StampFailureMetadata(envelope, exception);
            e.Exception = exception;
            await _deadLetter.PostAsync(e);
        }
    }

    public async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var messages =
            await _sessionReceiver.ReceiveMessagesAsync(_endpoint.MaximumMessagesToReceive, _endpoint.MaximumWaitTime,
                cancellationToken);

        foreach (var message in messages)
        {
            // GH-5257. The listener is stopping: don't start the rest of the batch. The messages left here are
            // never settled, so the session lock is released with them when the session receiver is disposed,
            // and the broker redelivers them -- in order, which re-sending them through _defer would not keep
            if (cancellationToken.IsCancellationRequested) break;

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
                    _logger.LogError(e,
                        "Failure to receive an incoming message with {Id}, trying to 'Defer' the message", envelope.Id);

                    try
                    {
                        await DeferAsync(envelope);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogError(exception, "Failure trying to Nack a previously failed message {Id}",
                            envelope.Id);
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
}