using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Transports;

namespace Wolverine.Runtime.WorkerQueues;

internal class InlineReceiver : IReceiver, ILatchedReceiver
{
    private readonly ILogger _logger;
    private readonly Endpoint _endpoint;
    private readonly IHandlerPipeline _pipeline;
    private readonly DurabilitySettings _settings;

    /// <summary>
    /// GH-3710. Null unless the endpoint opted in with WithInMemoryIdempotency().
    /// </summary>
    private readonly IIncomingIdempotencyGuard? _idempotency;

    private int _inFlightCount;
    private readonly TaskCompletionSource _drainComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _latched;

    public InlineReceiver(Endpoint endpoint, IWolverineRuntime runtime, IHandlerPipeline pipeline)
    {
        _endpoint = endpoint;
        _pipeline = pipeline;
        _logger = runtime.LoggerFactory.CreateLogger<InlineReceiver>();
        _settings = runtime.DurabilitySettings;
        _idempotency = endpoint.IdempotencyGuard;
    }

    public IHandlerPipeline Pipeline => _pipeline;

    public int QueueCount => Volatile.Read(ref _inFlightCount);

    public void Dispose()
    {
        // Nothing
    }

    public void Latch()
    {
        _latched = true;
    }

    public ValueTask DrainAsync()
    {
        // If _latched was already true, this drain was triggered during shutdown
        // (after StopAndDrainAsync called LatchReceiver()). Safe to wait for in-flight items.
        // If _latched was false, this drain may have been triggered from within the handler
        // pipeline (e.g., rate limiting pause via PauseListenerContinuation). Waiting for
        // in-flight items to complete would deadlock because the current message's
        // execute function is still on the call stack.
        var waitForCompletion = _latched;
        _latched = true;

        if (!waitForCompletion)
        {
            return ValueTask.CompletedTask;
        }

        if (Volatile.Read(ref _inFlightCount) == 0)
        {
            _drainComplete.TrySetResult();
        }

        return new ValueTask(_drainComplete.Task.WaitAsync(_settings.DrainTimeout));
    }

    public async ValueTask ReceivedAsync(IListener listener, Envelope[] messages)
    {
        if (messages.Length == 0) return;

        Interlocked.Add(ref _inFlightCount, messages.Length);

        foreach (var envelope in messages)
        {
            try
            {
                await ProcessMessageAsync(listener, envelope);
            }
            finally
            {
                DecrementInFlightCount();
            }
        }
    }

    public async ValueTask ReceivedAsync(IListener listener, Envelope envelope)
    {
        Interlocked.Increment(ref _inFlightCount);

        try
        {
            await ProcessMessageAsync(listener, envelope);
        }
        finally
        {
            DecrementInFlightCount();
        }
    }

    private async ValueTask ProcessMessageAsync(IListener listener, Envelope envelope)
    {
        if (_latched && (!_endpoint.ProcessInlineWhileDraining || _drainComplete.Task.IsCompleted))
        {
            try
            {
                await listener.DeferAsync(envelope);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error deferring envelope {EnvelopeId} after latch", envelope.Id);
            }

            return;
        }

        using var activity = _endpoint.TelemetryEnabled ? WolverineTracing.StartReceiving(envelope) : null;

        try
        {
            envelope.MarkReceived(listener, DateTimeOffset.UtcNow, _settings, _endpoint.WireTap);

            // GH-3710. Ack-and-drop a redelivery of something already handled within the window, the
            // non-durable analogue of DurableReceiver.handleDuplicateIncomingEnvelope.
            if (_idempotency != null && !_idempotency.TryBeginProcessing(envelope))
            {
                _logger.LogDebug(
                    "Discarding duplicate delivery of envelope {EnvelopeId} ({MessageType}) at {Uri}; it was already handled within the in-memory idempotency window",
                    envelope.Id, envelope.MessageType, listener.Address);

                await listener.CompleteAsync(envelope);
                return;
            }

            await _pipeline.InvokeAsync(envelope, listener, activity!);

            // GH-3710. Only a delivery the broker will not redeliver -- acked, or natively dead lettered --
            // may be remembered. A nack or requeue releases the id so the redelivery still runs.
            recordOutcome(envelope);
            _logger.IncomingReceived(envelope, listener.Address);

            // Don't clobber an Error status already set by the HandlerPipeline / Executor.
            // When a message fails but the failure is contained by an error-handling
            // continuation (dead-letter, discard, retries exhausted, ...), no exception
            // propagates out of InvokeAsync, so this success path still runs. Only mark
            // Ok when the activity isn't already flagged as an Error. See GH-3288.
            if (activity is { Status: not ActivityStatusCode.Error })
            {
                activity.SetStatus(ActivityStatusCode.Ok);
            }
        }
        catch (Exception? e)
        {
            _idempotency?.Release(envelope);

            activity?.SetStatus(ActivityStatusCode.Error, e.GetType().Name);
            _logger.LogError(e, "Failure to receive an incoming message for envelope {EnvelopeId}", envelope.Id);

            try
            {
                await listener.DeferAsync(envelope);
            }
            catch (Exception? ex)
            {
                _logger.LogError(ex,
                    "Error when trying to Nack a Rabbit MQ message that failed in the HandlerPipeline ({ConversationId})",
                    envelope.CorrelationId);
            }
        }
        finally
        {
            activity?.Stop();
        }
    }

    private void recordOutcome(Envelope envelope)
    {
        if (_idempotency == null) return;

        if (envelope.HasBeenAcked)
        {
            _idempotency.MarkProcessed(envelope);
        }
        else
        {
            _idempotency.Release(envelope);
        }
    }

    private void DecrementInFlightCount()
    {
        if (Interlocked.Decrement(ref _inFlightCount) == 0 && _latched)
        {
            _drainComplete.TrySetResult();
        }
    }
}
