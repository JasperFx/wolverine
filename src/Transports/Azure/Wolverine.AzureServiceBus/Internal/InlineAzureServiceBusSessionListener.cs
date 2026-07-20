using Azure.Messaging.ServiceBus;
using JasperFx.Blocks;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AzureServiceBus.Internal;

/// <summary>
/// Session-enabled listener built on the Azure SDK's <see cref="ServiceBusSessionProcessor" />. This is
/// used in place of the hand-rolled AcceptNextSession loop (AzureServiceBusSessionListener) whenever the
/// endpoint has ConfigureSessionProcessor customization — most notably when SessionIds is populated to pin
/// the listener to a fixed set of session identifiers (GH-3533). Mirrors InlineAzureServiceBusListener's
/// acknowledgement, dead lettering, native scheduling, and connection-state reporting.
/// </summary>
public class InlineAzureServiceBusSessionListener : IListener, ISupportDeadLetterQueue, ISupportNativeScheduling,
    IReportConnectionState
{
    private readonly CancellationTokenSource _cancellation = new();

    // GH-3237: derived only from ProcessErrorAsync (degrade-only). A successful delivery clears back to
    // Unknown — never Connected, because the SDK cannot prove the AMQP link is up without traffic.
    private volatile TransportConnectionState _connectionState = TransportConnectionState.Unknown;
    private readonly RetryBlock<AzureServiceBusEnvelope> _complete;
    private readonly RetryBlock<AzureServiceBusEnvelope> _deadLetter;
    private readonly RetryBlock<AzureServiceBusEnvelope> _defer;
    private readonly AzureServiceBusEndpoint _endpoint;
    private readonly ILogger _logger;
    private readonly IIncomingMapper<ServiceBusReceivedMessage> _mapper;
    private readonly ServiceBusSessionProcessor _processor;
    private readonly IReceiver _receiver;
    private readonly ISender _requeue;

    public InlineAzureServiceBusSessionListener(AzureServiceBusEndpoint endpoint,
        ILogger logger,
        ServiceBusSessionProcessor processor, IReceiver receiver,
        IIncomingMapper<ServiceBusReceivedMessage> mapper,
        ISender requeue)
    {
        _endpoint = endpoint;
        _logger = logger;
        _processor = processor;
        _receiver = receiver;
        _mapper = mapper;
        _requeue = requeue;

        _complete = new RetryBlock<AzureServiceBusEnvelope>((e, _) => { return e.CompleteAsync(_cancellation.Token); },
            _logger, _cancellation.Token);

        _defer = new RetryBlock<AzureServiceBusEnvelope>(async (envelope, _) =>
        {
            if (envelope is { } e)
            {
                await e.CompleteAsync(_cancellation.Token);
                e.IsCompleted = true;
            }

            await _requeue.SendAsync(envelope);
        }, logger, _cancellation.Token);

        _deadLetter =
            new RetryBlock<AzureServiceBusEnvelope>(
                (e, c) => e.DeadLetterAsync(_cancellation.Token, e.Exception?.GetType().NameInCode(),
                    e.Exception?.Message), logger,
                _cancellation.Token);

        _processor.ProcessMessageAsync += processMessageAsync;
        _processor.ProcessErrorAsync += processErrorAsync;
    }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    public TransportConnectionState ConnectionState => _connectionState;

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

    public async Task<bool> TryRequeueAsync(Envelope envelope)
    {
        if (envelope is AzureServiceBusEnvelope e)
        {
            await _defer.PostAsync(e);
            return true;
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        _cancellation.Dispose();
        _complete.SafeDispose();
        _defer.SafeDispose();
        _deadLetter.SafeDispose();
        await _processor.DisposeAsync();
    }

    public Uri Address => _endpoint.Uri;

    public async ValueTask StopAsync()
    {
        await _processor.StopProcessingAsync();
    }

    public async Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        if (envelope is AzureServiceBusEnvelope e)
        {
            DeadLetterQueueConstants.StampFailureMetadata(envelope, exception);
            e.Exception = exception;
            await _deadLetter.PostAsync(e);
        }
    }

    public bool NativeDeadLetterQueueEnabled => true;

    public async Task MoveToScheduledUntilAsync(Envelope envelope, DateTimeOffset time)
    {
        envelope.ScheduledTime = time;
        await _requeue.SendAsync(envelope);
    }

    public Task StartAsync()
    {
        return _processor.StartProcessingAsync();
    }

    private Task processErrorAsync(ProcessErrorEventArgs arg)
    {
        var degraded = AzureServiceBusConnectionStateMapper.StateForError(arg.Exception);
        if (degraded.HasValue)
        {
            _connectionState = degraded.Value;
        }

        _logger.LogError(arg.Exception, "Error trying to receive Azure Service Bus message at {Uri}", _endpoint.Uri);
        return Task.CompletedTask;
    }

    private async Task processMessageAsync(ProcessSessionMessageEventArgs arg)
    {
        if (_connectionState != TransportConnectionState.Unknown)
        {
            // Messages are flowing again, so any previously derived trouble state is stale
            _connectionState = TransportConnectionState.Unknown;
        }

        try
        {
            var envelope = new AzureServiceBusEnvelope(arg);
            _mapper.MapIncomingToEnvelope(envelope, arg.Message);

            // If a ping, you're done, ack it and get out of there
            if (envelope.IsPing())
            {
                await CompleteAsync(envelope);
                return;
            }

            try
            {
                await _receiver.ReceivedAsync(this, envelope);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failure to receive an incoming message with {Id}, trying to 'Defer' the message",
                    envelope.Id);

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
            await _deadLetter.PostAsync(new AzureServiceBusEnvelope(arg) { Exception = e });
            _logger.LogError(e, "Error while reading message {Id} from {Uri}", arg.Message.MessageId,
                _endpoint.Uri);
        }
    }
}
