using System.Diagnostics;
using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Runtime.Partitioning;
using Wolverine.Runtime.Scheduled;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Sending;

namespace Wolverine.Runtime.WorkerQueues;

internal class BufferedReceiver : ILocalQueue, IChannelCallback, ISupportNativeScheduling, ISupportDeadLetterQueue
{
    private readonly RetryBlock<Envelope> _completeBlock;

    private readonly ISender? _deadLetterSender;
    private readonly RetryBlock<Envelope> _deferBlock;
    private readonly Endpoint _endpoint;
    private readonly IWolverineRuntime _runtime;
    private readonly ILogger _logger;
    private readonly RetryBlock<Envelope>? _moveToErrors;
    private readonly IBlock<Envelope> _receivingBlock;
    private readonly InMemoryScheduledJobProcessor _scheduler;
    private readonly DurabilitySettings _settings;
    private bool _latched;

    public BufferedReceiver(Endpoint endpoint, IWolverineRuntime runtime, IHandlerPipeline pipeline)
    {
        _endpoint = endpoint;
        _runtime = runtime;
        Uri = endpoint.Uri;
        _logger = runtime.LoggerFactory.CreateLogger<BufferedReceiver>();
        _settings = runtime.DurabilitySettings;
        Pipeline = pipeline;

        _scheduler = new InMemoryScheduledJobProcessor(this, _logger);

        _deferBlock = new RetryBlock<Envelope>((env, _) => env.Listener!.DeferAsync(env).AsTask(), runtime.Logger,
            runtime.Cancellation);
        _completeBlock = new RetryBlock<Envelope>((env, _) => env.Listener!.CompleteAsync(env).AsTask(), runtime.Logger,
            runtime.Cancellation);

        _receivingBlock = endpoint.GroupShardingSlotNumber == null  
            ? new Block<Envelope>(endpoint.MaxDegreeOfParallelism, executeAsync)
            : new ShardedExecutionBlock((int)endpoint.GroupShardingSlotNumber, runtime.Options.MessagePartitioning, executeAsync).DeserializeFirst(pipeline, runtime, this);

        if (endpoint.TryBuildDeadLetterSender(runtime, out var dlq))
        {
            _deadLetterSender = dlq;

            _moveToErrors = new RetryBlock<Envelope>(
                async (envelope, _) => { await _deadLetterSender!.SendAsync(envelope).ConfigureAwait(false); }, _logger,
                _settings.Cancellation);
        }
    }

    internal async Task executeAsync(Envelope envelope, CancellationToken _)
    {
        if (_latched && envelope.Listener != null)
        {
            await _deferBlock.PostAsync(envelope).ConfigureAwait(false);
            return;
        }

        try
        {
            if (envelope.ContentType.IsEmpty())
            {
                envelope.ContentType = EnvelopeConstants.JsonContentType;
            }

            await Pipeline!.InvokeAsync(envelope, this).ConfigureAwait(false);
        }
        catch (Exception? e)
        {
            // This *should* never happen, but of course it will
            _logger.LogError(e, "Unexpected error in Pipeline invocation");
        }
    }

    ValueTask IChannelCallback.CompleteAsync(Envelope envelope)
    {
        // When the durability agent recovers a persisted envelope and dispatches it to a
        // non-durable local queue (DLQ replay per GH-1942, or scheduled-message firing),
        // BufferedLocalQueue.EnqueueDirectlyAsync attaches a LocalQueueRecoveryListener so
        // that successful pipeline completion marks the inbox row Handled. Without this,
        // the row sits in wolverine_incoming forever.
        if (envelope.Listener is LocalQueueRecoveryListener recovery)
        {
            return recovery.CompleteAsync(envelope);
        }

        return ValueTask.CompletedTask;
    }

    async ValueTask IChannelCallback.DeferAsync(Envelope envelope)
    {
        if (envelope.Listener == null)
        {
            await EnqueueAsync(envelope).ConfigureAwait(false);
            return;
        }

        try
        {
            var nativelyRequeued = await envelope.Listener.TryRequeueAsync(envelope).ConfigureAwait(false);
            if (!nativelyRequeued)
            {
                await EnqueueAsync(envelope).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to use native dead letter queue for {Uri}", Uri);
            await EnqueueAsync(envelope).ConfigureAwait(false);
        }
    }

    public IHandlerPipeline Pipeline { get; } = null!;

    public Uri Uri { get; }

    public int QueueCount => (int)_receivingBlock.Count;

    /// <summary>
    /// Immediately latch to stop processing new messages without draining.
    /// </summary>
    public void Latch()
    {
        _latched = true;
    }

    public async ValueTask DrainAsync()
    {
        // If _latched was already true, this drain was triggered during shutdown
        // (after StopAndDrainAsync called Latch()). Safe to wait for in-flight items.
        // If _latched was false, this drain may have been triggered from within the handler
        // pipeline (e.g., rate limiting pause via PauseListenerContinuation). Waiting for
        // the receiving block to complete would deadlock because the current message's
        // execute function is still on the call stack.
        var waitForCompletion = _latched;
        _latched = true;
        _receivingBlock.Complete();

        if (waitForCompletion)
        {
            try
            {
                var completion = _receivingBlock.WaitForCompletionAsync();
                await Task.WhenAny(completion, Task.Delay(_settings.DrainTimeout)).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "Error waiting for in-flight message processing to complete at {Uri}", Uri);
            }
        }

        await _completeBlock.DrainAsync().ConfigureAwait(false);
        await _deferBlock.DrainAsync().ConfigureAwait(false);

        if (_moveToErrors != null)
        {
            await _moveToErrors.DrainAsync().ConfigureAwait(false);
        }
    }

    public void Enqueue(Envelope envelope)
    {
        if (envelope.IsPing())
        {
            return;
        }

        var activity = _endpoint.TelemetryEnabled ? WolverineTracing.StartReceiving(envelope) : null;
        _receivingBlock.Post(envelope);
        activity?.Stop();
    }

    public async ValueTask EnqueueAsync(Envelope envelope)
    {
        if (envelope.IsPing())
        {
            return;
        }

        var activity = _endpoint.TelemetryEnabled ? WolverineTracing.StartReceiving(envelope) : null;
        await _receivingBlock.PostAsync(envelope).ConfigureAwait(false);
        activity?.Stop();
    }

    async ValueTask IReceiver.ReceivedAsync(IListener listener, Envelope[] messages)
    {
        var now = DateTimeOffset.Now;

        if (_settings.Cancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException();
        }

        foreach (var envelope in messages)
        {
            envelope.MarkReceived(listener, now, _settings, _endpoint.WireTap);
            if (!envelope.IsExpired())
            {
                await EnqueueAsync(envelope).ConfigureAwait(false);
            }

            await _completeBlock.PostAsync(envelope).ConfigureAwait(false);
        }

        _logger.IncomingBatchReceived(Uri, messages);
    }

    public async ValueTask ReceivedAsync(IListener listener, Envelope envelope)
    {
        var now = DateTimeOffset.Now;
        envelope.MarkReceived(listener, now, _settings, _endpoint.WireTap);

        if (envelope.IsExpired())
        {
            await _completeBlock.PostAsync(envelope).ConfigureAwait(false);
            return;
        }

        if (envelope.Status == EnvelopeStatus.Scheduled)
        {
            _scheduler.Enqueue(envelope.ScheduledTime!.Value, envelope);
        }
        else
        {
            await EnqueueAsync(envelope).ConfigureAwait(false);
        }

        await _completeBlock.PostAsync(envelope).ConfigureAwait(false);

        _logger.IncomingReceived(envelope, Uri);
    }

    public void Dispose()
    {
        _receivingBlock.Complete();
        _completeBlock.Dispose();
        _deferBlock.Dispose();

        if (_deadLetterSender is IDisposable d)
        {
            d.SafeDispose();
        }

        _moveToErrors?.Dispose();
    }

    public Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        DeadLetterQueueConstants.StampFailureMetadata(envelope, exception);
        return _moveToErrors!.PostAsync(envelope);
    }

    public bool NativeDeadLetterQueueEnabled => _deadLetterSender != null;

    Task ISupportNativeScheduling.MoveToScheduledUntilAsync(Envelope envelope, DateTimeOffset time)
    {
        _logger.LogDebug("Moving envelope {EnvelopeId} ({MessageType}) to scheduled status in buffered receiver until {ScheduledTime}", envelope.Id, envelope.MessageType, time);
        envelope.ScheduledTime = time;
        ScheduleExecution(envelope);

        return Task.CompletedTask;
    }

    public void ScheduleExecution(Envelope envelope)
    {
        if (!envelope.ScheduledTime.HasValue)
        {
            throw new ArgumentOutOfRangeException(nameof(envelope),
                $"There is no {nameof(Envelope.ScheduledTime)} value");
        }

        _logger.LogDebug("Scheduling envelope {EnvelopeId} ({MessageType}) execution via in-memory scheduler at {ExecutionTime}", envelope.Id, envelope.MessageType, envelope.ScheduledTime.Value);
        _scheduler.Enqueue(envelope.ScheduledTime.Value, envelope);
    }
}