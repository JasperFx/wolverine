using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Baseline;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Logging;

namespace Wolverine.Transports.Sending;

internal abstract class SendingAgent : ISendingAgent, ISenderCallback, ICircuit, IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly IMessageLogger _messageLogger;
    protected readonly ISender _sender;

    protected readonly Func<Envelope, Task> _senderDelegate;

    private readonly ActionBlock<Envelope> _sending;
    protected readonly AdvancedSettings _settings;
    private CircuitWatcher? _circuitWatcher;
    private int _failureCount;


    public SendingAgent(ILogger logger, IMessageLogger messageLogger, ISender sender, AdvancedSettings settings,
        Endpoint endpoint)
    {
        _logger = logger;
        _messageLogger = messageLogger;
        _sender = sender;
        _settings = settings;
        Endpoint = endpoint;

        _senderDelegate = sendWithExplicitHandlingAsync;
        if (_sender is ISenderRequiresCallback)
        {
            _senderDelegate = sendWithCallbackHandlingAsync;
        }


        _sending = new ActionBlock<Envelope>(_senderDelegate, Endpoint.ExecutionOptions);
    }

    public Task<bool> TryToResumeAsync(CancellationToken cancellationToken)
    {
        return _sender.PingAsync();
    }

    TimeSpan ICircuit.RetryInterval => Endpoint.PingIntervalForCircuitResume;

    Task ICircuit.ResumeAsync(CancellationToken cancellationToken)
    {
        _circuitWatcher?.SafeDispose();
        _circuitWatcher = null;

        Unlatch();

        return afterRestartingAsync(_sender);
    }


    Task ISenderCallback.MarkTimedOutAsync(OutgoingMessageBatch outgoing)
    {
        _logger.OutgoingBatchFailed(outgoing);
        return markFailedAsync(outgoing);
    }

    Task ISenderCallback.MarkSerializationFailureAsync(OutgoingMessageBatch outgoing)
    {
        _logger.OutgoingBatchFailed(outgoing);
        // Can't really happen now, but what the heck.
        var exception = new Exception("Serialization failure with outgoing envelopes " +
                                      outgoing.Messages.Select(x => x.ToString()).Join(", "));
        _logger.LogError(exception, "Serialization failure");

        return Task.CompletedTask;
    }

    Task ISenderCallback.MarkQueueDoesNotExistAsync(OutgoingMessageBatch outgoing)
    {
        _logger.OutgoingBatchFailed(outgoing, new QueueDoesNotExistException(outgoing));

        return Task.CompletedTask;
    }

    Task ISenderCallback.MarkProcessingFailureAsync(OutgoingMessageBatch outgoing)
    {
        _logger.OutgoingBatchFailed(outgoing);
        return markFailedAsync(outgoing);
    }

    public Task MarkProcessingFailureAsync(OutgoingMessageBatch outgoing, Exception? exception)
    {
        _logger.LogError(exception,
            "Failure trying to send a message batch to {Destination}", outgoing.Destination);
        _logger.OutgoingBatchFailed(outgoing, exception);
        return markFailedAsync(outgoing);
    }

    Task ISenderCallback.MarkSenderIsLatchedAsync(OutgoingMessageBatch outgoing)
    {
        return markFailedAsync(outgoing);
    }

    public abstract Task MarkSuccessfulAsync(OutgoingMessageBatch outgoing);

    public Endpoint Endpoint { get; }

    public Uri? ReplyUri { get; set; }

    public Uri Destination => _sender.Destination;

    public bool Latched { get; private set; }
    public abstract bool IsDurable { get; }

    public ValueTask EnqueueOutgoingAsync(Envelope envelope)
    {
        setDefaults(envelope);
        _sending.Post(envelope);
        _messageLogger.Sent(envelope);

        return ValueTask.CompletedTask;
    }

    public async ValueTask StoreAndForwardAsync(Envelope envelope)
    {
        setDefaults(envelope);

        await storeAndForwardAsync(envelope);

        _messageLogger.Sent(envelope);
    }

    public bool SupportsNativeScheduledSend => _sender.SupportsNativeScheduledSend;

    private void setDefaults(Envelope envelope)
    {
        envelope.Status = EnvelopeStatus.Outgoing;
        envelope.OwnerId = _settings.UniqueNodeId;
        envelope.ReplyUri = envelope.ReplyUri ?? ReplyUri;
    }

    protected abstract Task storeAndForwardAsync(Envelope envelope);

    protected abstract Task afterRestartingAsync(ISender sender);

    public abstract Task MarkSuccessfulAsync(Envelope outgoing);

    public Task LatchAndDrainAsync()
    {
        Latched = true;

        _sending.Complete();

        _logger.CircuitBroken(Destination);

        return Task.CompletedTask;
    }

    public void Unlatch()
    {
        _logger.CircuitResumed(Destination);

        Latched = false;
    }

    private async Task sendWithCallbackHandlingAsync(Envelope envelope)
    {
        try
        {
            await _sender.SendAsync(envelope);
        }
        catch (Exception e)
        {
            try
            {
                await MarkProcessingFailureAsync(envelope, e);
            }
            catch (Exception? exception)
            {
                _logger.LogError(exception, "Error while trying to process a failure");
            }
        }
    }

    private async Task sendWithExplicitHandlingAsync(Envelope envelope)
    {
        try
        {
            await _sender.SendAsync(envelope);

            await MarkSuccessfulAsync(envelope);
        }
        catch (Exception e)
        {
            try
            {
                await MarkProcessingFailureAsync(envelope, e);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error while trying to process a batch send failure");
            }
        }
    }

    private async Task markFailedAsync(OutgoingMessageBatch batch)
    {
        // If it's already latched, just enqueue again
        if (Latched)
        {
            await EnqueueForRetryAsync(batch);
            return;
        }

        _failureCount++;

        if (_failureCount >= Endpoint.FailuresBeforeCircuitBreaks)
        {
            await LatchAndDrainAsync();
            await EnqueueForRetryAsync(batch);

            _circuitWatcher = new CircuitWatcher(this, _settings.Cancellation);
        }
        else
        {
            foreach (var envelope in batch.Messages)
            {
#pragma warning disable 4014
                _senderDelegate(envelope);
#pragma warning restore 4014
            }
        }
    }


    public abstract Task EnqueueForRetryAsync(OutgoingMessageBatch batch);


    public Task MarkSuccessAsync()
    {
        _failureCount = 0;
        Unlatch();
        _circuitWatcher?.SafeDispose();
        _circuitWatcher = null;

        return Task.CompletedTask;
    }

    public Task MarkProcessingFailureAsync(Envelope outgoing, Exception? exception)
    {
        if (outgoing.Destination == null)
        {
            throw new InvalidOperationException("This envelope has not been routed");
        }

        var batch = new OutgoingMessageBatch(outgoing.Destination, new[] { outgoing });
        _logger.OutgoingBatchFailed(batch, exception);
        return markFailedAsync(batch);
    }

    public ValueTask DisposeAsync()
    {
        if (_sender is IAsyncDisposable ad) return ad.DisposeAsync();

        if (_sender is IDisposable d) d.SafeDispose();

        return ValueTask.CompletedTask;
    }
}
