using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Util.Dataflow;

namespace Wolverine.Transports.Sending;

public abstract class SendingAgent : ISendingAgent, ISenderCallback, ISenderCircuit, IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly IMessageTracker _messageLogger;
    protected readonly ISender _sender;

    protected readonly RetryBlock<Envelope> _sending;
    protected readonly DurabilitySettings _settings;
    private CircuitWatcher? _circuitWatcher;
    private int _failureCount;
    private readonly SemaphoreSlim _failureCountLock = new SemaphoreSlim(1, 1);


    public SendingAgent(ILogger logger, IMessageTracker messageLogger, ISender sender, DurabilitySettings settings,
        Endpoint endpoint)
    {
        _logger = logger;
        _messageLogger = messageLogger;
        _sender = sender;
        _settings = settings;
        Endpoint = endpoint;

        Func<Envelope, CancellationToken, Task> senderDelegate = _sender is ISenderRequiresCallback
            ? sendWithCallbackHandlingAsync
            : sendWithExplicitHandlingAsync;

        _sending = new RetryBlock<Envelope>(senderDelegate, logger, _settings.Cancellation, Endpoint.ExecutionOptions);
    }

    public ISender Sender => _sender;

    public virtual ValueTask DisposeAsync()
    {
        if (_sender is IAsyncDisposable ad)
        {
            return ad.DisposeAsync();
        }

        if (_sender is IDisposable d)
        {
            d.SafeDispose();
        }

        _sending.Dispose();

        return ValueTask.CompletedTask;
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

    public Task<bool> TryToResumeAsync(CancellationToken cancellationToken)
    {
        return _sender.PingAsync();
    }

    TimeSpan ISenderCircuit.RetryInterval => Endpoint.PingIntervalForCircuitResume;

    async Task ISenderCircuit.ResumeAsync(CancellationToken cancellationToken)
    {
        using var activity = WolverineTracing.ActivitySource.StartActivity(WolverineTracing.SendingResumed);
        activity?.SetTag(WolverineTracing.EndpointAddress, Endpoint.Uri);

        await MarkSuccessAsync();

        await executeWithRetriesAsync(() => afterRestartingAsync(_sender));
    }

    public Endpoint Endpoint { get; }

    public Uri? ReplyUri { get; set; }

    public Uri Destination => _sender.Destination;

    public bool Latched { get; private set; }
    public abstract bool IsDurable { get; }

    public async ValueTask EnqueueOutgoingAsync(Envelope envelope)
    {
        setDefaults(envelope);
        await _sending.PostAsync(envelope);
        _messageLogger.Sent(envelope);
    }

    public async ValueTask StoreAndForwardAsync(Envelope envelope)
    {
        setDefaults(envelope);

        await storeAndForwardAsync(envelope);

        _messageLogger.Sent(envelope);
    }

    public bool SupportsNativeScheduledSend => _sender.SupportsNativeScheduledSend;

    protected async Task executeWithRetriesAsync(Func<Task> action)
    {
        var i = 0;
        while (true)
        {
            try
            {
                await action().ConfigureAwait(false);
                return;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Unexpected failure");
                i++;
                await Task.Delay(i * 100).ConfigureAwait(false);
            }
        }
    }

    private void setDefaults(Envelope envelope)
    {
        envelope.Status = EnvelopeStatus.Outgoing;
        envelope.OwnerId = _settings.AssignedNodeNumber;
        envelope.ReplyUri ??= ReplyUri;
    }

    protected abstract Task storeAndForwardAsync(Envelope envelope);

    protected abstract Task afterRestartingAsync(ISender sender);

    public abstract Task MarkSuccessfulAsync(Envelope outgoing);

    public async Task LatchAndDrainAsync()
    {
        Latched = true;

        try
        {
            await drainOtherAsync();
            await _sending.DrainAsync();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while trying to drain the outgoing sender for {Uri}", Destination);
        }

        _logger.CircuitBroken(Destination);
    }

    protected virtual Task drainOtherAsync()
    {
        return Task.CompletedTask;
    }

    public void Unlatch()
    {
        if (Latched)
        {
            _logger.CircuitResumed(Destination);
        }

        Latched = false;
    }

    private async Task sendWithCallbackHandlingAsync(Envelope envelope, CancellationToken token)
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

    private async Task sendWithExplicitHandlingAsync(Envelope envelope, CancellationToken token)
    {
        try
        {
            await _sender.SendAsync(envelope);

            await MarkSuccessfulAsync(envelope);
        }
        catch (NotSupportedException)
        {
            // Ignore it, most likely a failure ack that should not have been sent
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

        await _failureCountLock.WaitAsync();
        try
        {
            _failureCount++;

            if (_failureCount >= Endpoint.FailuresBeforeCircuitBreaks)
            {
                using var activity = WolverineTracing.ActivitySource.StartActivity(WolverineTracing.SendingPaused);
                activity?.SetTag(WolverineTracing.StopReason, WolverineTracing.TooManySenderFailures);
                activity?.SetTag(WolverineTracing.EndpointAddress, Endpoint.Uri);

                await LatchAndDrainAsync();
                await EnqueueForRetryAsync(batch);

                _circuitWatcher ??= new CircuitWatcher(this, _settings.Cancellation);
                return;
            }
        }
        finally
        {
            _failureCountLock.Release();
        }

        foreach (var envelope in batch.Messages) await _sending.PostAsync(envelope);
    }

    public abstract Task EnqueueForRetryAsync(OutgoingMessageBatch batch);

    public async Task MarkSuccessAsync()
    {
        await _failureCountLock.WaitAsync();
        try
        {
            _failureCount = 0;
            Unlatch();
            _circuitWatcher?.SafeDispose();
            _circuitWatcher = null;
        }
        finally
        {
            _failureCountLock.Release();
        }
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
}