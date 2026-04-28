using JasperFx.Blocks;
using JasperFx.Core;
using MassTransit;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Partitioning;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Runtime.WorkerQueues;

public class DurableReceiver : ILocalQueue, IChannelCallback, ISupportNativeScheduling, ISupportDeadLetterQueue,
    IAsyncDisposable
{
    private readonly RetryBlock<Envelope> _completeBlock;

    private readonly ISender? _deadLetterSender;
    private readonly RetryBlock<Envelope> _deferBlock;
    private readonly Endpoint _endpoint;
    private readonly IMessageInbox _inbox;
    private readonly RetryBlock<Envelope> _incrementAttempts;

    // ReSharper disable once InconsistentNaming
    protected readonly ILogger _logger;
    private readonly RetryBlock<Envelope> _markAsHandled;
    private readonly RetryBlock<Envelope> _moveToErrors;
    private readonly IBlock<Envelope> _receiver;
    private readonly RetryBlock<Envelope> _receivingOne;
    private readonly IWolverineRuntime _runtime;
    private readonly RetryBlock<Envelope> _scheduleExecution;
    private readonly DurabilitySettings _settings;

    // These members are for draining
    private bool _latched;
    private int _inboxUnavailableSignaled;

    public DurableReceiver(Endpoint endpoint, IWolverineRuntime runtime, IHandlerPipeline pipeline)
    {
        _endpoint = endpoint;
        _runtime = runtime;
        _settings = runtime.DurabilitySettings;
        
        // the check for Stores being null is honestly just because of some tests that use a little too much mocking
        _inbox = runtime .Stores != null && runtime.Stores.HasAnyAncillaryStores() ? new DelegatingMessageInbox(runtime.Storage.Inbox, runtime.Stores) : runtime.Storage.Inbox;
        _logger = runtime.LoggerFactory.CreateLogger<DurableReceiver>();

        Uri = endpoint.Uri;

        ShouldPersistBeforeProcessing = !(endpoint is IDatabaseBackedEndpoint);

        Pipeline = pipeline;

        Func<Envelope, CancellationToken, Task> execute = async (envelope, _) =>
        {
            if (_latched)
            {
                return;
            }

            try
            {
                envelope.ContentType ??= EnvelopeConstants.JsonContentType;

                await pipeline.InvokeAsync(envelope, this).ConfigureAwait(false);
            }
            catch (Exception? e)
            {
                if (_receiver != null)
                {
                    await _receiver.PostAsync(envelope).ConfigureAwait(false);
                }

                // This *should* never happen, but of course it will
                _logger.LogError(e, "Unexpected pipeline invocation error");
            }
        };
        
        _receiver = endpoint.GroupShardingSlotNumber == null 
            ? new Block<Envelope>(endpoint.MaxDegreeOfParallelism, execute)
            : new ShardedExecutionBlock((int)endpoint.GroupShardingSlotNumber, runtime.Options.MessagePartitioning, execute).DeserializeFirst(pipeline, runtime, this);
        
        _deferBlock = new RetryBlock<Envelope>((env, _) => env.Listener!.DeferAsync(env).AsTask(), runtime.Logger,
            runtime.Cancellation);
        _completeBlock = new RetryBlock<Envelope>((env, _) => env.Listener!.CompleteAsync(env).AsTask(), runtime.Logger,
            runtime.Cancellation);


        _markAsHandled = new RetryBlock<Envelope>(async (e, _) =>
            {
                // Little optimization. If the envelope has already been marked as handled
                // as part of transactional middleware, there's no need to mess w/ this
                if (e.Status == EnvelopeStatus.Handled) return;
                
                // Only care about the batch if one exists
                if (e.Batch != null)
                {
                    await _inbox.MarkIncomingEnvelopeAsHandledAsync(e.Batch).ConfigureAwait(false);
                }
                else
                {
                    await _inbox.MarkIncomingEnvelopeAsHandledAsync(e).ConfigureAwait(false);
                }
            }, _logger,
            _settings.Cancellation);

        _incrementAttempts = new RetryBlock<Envelope>((e, _) => _inbox.IncrementIncomingEnvelopeAttemptsAsync(e),
            _logger, _settings.Cancellation);

        if (endpoint is IDatabaseBackedEndpoint db)
        {
            _scheduleExecution = new RetryBlock<Envelope>((e, _) => db.ScheduleRetryAsync(e, _settings.Cancellation),
                _logger, _settings.Cancellation);
        }
        else
        {
            _scheduleExecution = new RetryBlock<Envelope>((e, _) => _inbox.ScheduleExecutionAsync(e),
                _logger, _settings.Cancellation);
        }

        _moveToErrors = new RetryBlock<Envelope>(
            async (envelope, _) =>
            {
                if (_deadLetterSender != null)
                {
                    await _deadLetterSender.SendAsync(envelope).ConfigureAwait(false);
                    return;
                }

                var report = new ErrorReport(envelope, envelope.Failure!);
                await _inbox.MoveToDeadLetterStorageAsync(report.Envelope, report.Exception).ConfigureAwait(false);
            }, _logger,
            _settings.Cancellation);

        _receivingOne = new RetryBlock<Envelope>((e, _) => receiveOneAsync(e), _logger, _settings.Cancellation);

        if (endpoint.TryBuildDeadLetterSender(runtime, out var dlq))
        {
            _deadLetterSender = dlq;
        }
    }

    public bool ShouldPersistBeforeProcessing { get; set; }

    /// <summary>
    /// If the handler for this message type targets an ancillary store on a
    /// different database, set envelope.Store so that the DelegatingMessageInbox
    /// persists it in the correct store for transactional atomicity.
    /// </summary>
    private void assignAncillaryStoreIfNeeded(Envelope envelope)
    {
        if (_runtime.Stores == null) return;
        if (envelope.Store != null) return; // already stamped (e.g. from Option B at read time)
        var store = _runtime.Stores.TryFindAncillaryStoreForMessageType(envelope.MessageType);
        if (store != null)
        {
            envelope.Store = store;
        }
    }

    private void assignAncillaryStoreIfNeeded(IReadOnlyList<Envelope> envelopes)
    {
        if (_runtime.Stores == null) return;
        foreach (var envelope in envelopes)
        {
            assignAncillaryStoreIfNeeded(envelope);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _receiver.WaitForCompletionAsync().ConfigureAwait(false);

        _incrementAttempts.Dispose();
        _scheduleExecution.Dispose();
        _markAsHandled.Dispose();
        _moveToErrors.Dispose();
        _receivingOne.Dispose();

        if (_deadLetterSender is IDisposable d)
        {
            d.SafeDispose();
        }

        _moveToErrors.Dispose();

        _completeBlock.Dispose();
        _deferBlock.Dispose();
    }

    public async ValueTask CompleteAsync(Envelope envelope)
    {
        if (envelope.InBatch)
        {
            return;
        }

        if (envelope.Batch != null)
        {
            foreach (var child in envelope.Batch)
            {
                child.InBatch = false;
                await _markAsHandled.PostAsync(child).ConfigureAwait(false);
            }
        }
        else
        {
            await _markAsHandled.PostAsync(envelope).ConfigureAwait(false);
        }
    }

    public async ValueTask DeferAsync(Envelope envelope)
    {
        // GH-826, the attempts are already incremented from the executor
        if (!envelope.IsFromLocalDurableQueue())
        {
            envelope.Attempts++;
        }

        await _incrementAttempts.PostAsync(envelope).ConfigureAwait(false);

        if (_latched)
        {
            if (envelope.Listener != null)
            {
                await _deferBlock.PostAsync(envelope).ConfigureAwait(false);
            }

            return;
        }

        await EnqueueAsync(envelope).ConfigureAwait(false);
    }

    public IHandlerPipeline Pipeline { get; } = null!;

    public Uri Uri { get; set; }

    public int QueueCount => (int)_receiver.Count;

    public void Enqueue(Envelope envelope)
    {
        envelope.ReplyUri = envelope.ReplyUri ?? Uri;
        // Envelopes can enter the queue without going through the listener
        // arrival paths (receiveOneAsync / ProcessReceivedMessagesAsync) — for
        // example via the scheduled-jobs poller's EnqueueDirectlyAsync. Make
        // sure the ancillary-store routing is applied here too so the
        // mark-as-handled SQL goes to the correct store. See GH-2576.
        assignAncillaryStoreIfNeeded(envelope);
        _receiver.Post(envelope);
    }

    public ValueTask EnqueueAsync(Envelope envelope)
    {
        envelope.WasPersistedInInbox = true;
        envelope.ReplyUri = envelope.ReplyUri ?? Uri;
        // See note on Enqueue — same reason. The scheduled-jobs poller in
        // {DatabaseFlavour}MessageStore.PollForScheduledMessagesAsync calls
        // runtime.EnqueueDirectlyAsync, which lands here without ever passing
        // through the assignAncillaryStoreIfNeeded calls in receiveOneAsync /
        // ProcessReceivedMessagesAsync. See GH-2576.
        assignAncillaryStoreIfNeeded(envelope);
        return _receiver.PostAsync(envelope);
    }

    public ValueTask ReceivedAsync(IListener listener, Envelope[] messages)
    {
        var now = DateTimeOffset.UtcNow;

        return ProcessReceivedMessagesAsync(now, listener, messages);
    }

    public async ValueTask ReceivedAsync(IListener listener, Envelope envelope)
    {
        if (listener == null)
        {
            throw new ArgumentNullException(nameof(listener));
        }

        if (envelope == null)
        {
            throw new ArgumentNullException(nameof(envelope));
        }

        if (_latched && !envelope.IsFromLocalDurableQueue())
        {
            if (envelope.Listener != null)
            {
                await _deferBlock.PostAsync(envelope).ConfigureAwait(false);
            }

            return;
        }

        if (envelope.IsExpired())
        {
            if (envelope.Listener != null)
            {
                await _completeBlock.PostAsync(envelope).ConfigureAwait(false);
            }

            return;
        }

        using var activity = _endpoint.TelemetryEnabled ? WolverineTracing.StartReceiving(envelope) : null;
        try
        {
            var now = DateTimeOffset.UtcNow;
            envelope.MarkReceived(listener, now, _settings, _endpoint.WireTap);

            await _receivingOne.PostAsync(envelope).ConfigureAwait(false);
        }
        finally
        {
            activity?.Stop();
        }
    }

    public async ValueTask DrainAsync()
    {
        // If _latched was already true, this drain was triggered during shutdown
        // (after StopAndDrainAsync called Latch()). Safe to wait for in-flight items.
        // If _latched was false, this drain may have been triggered from within the handler
        // pipeline (e.g., rate limiting pause via PauseListenerContinuation). Waiting for
        // the receiver block to complete would deadlock because the current message's
        // execute function is still on the call stack.
        var waitForCompletion = _latched;
        _latched = true;
        _receiver.Complete();

        if (waitForCompletion)
        {
            try
            {
                var completion = _receiver.WaitForCompletionAsync();
                await Task.WhenAny(completion, Task.Delay(_settings.DrainTimeout)).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "Error waiting for in-flight message processing to complete at {Uri}", Uri);
            }
        }

        await _incrementAttempts.DrainAsync().ConfigureAwait(false);
        await _scheduleExecution.DrainAsync().ConfigureAwait(false);
        await _markAsHandled.DrainAsync().ConfigureAwait(false);
        await _moveToErrors.DrainAsync().ConfigureAwait(false);
        await _receivingOne.DrainAsync().ConfigureAwait(false);

        await _completeBlock.DrainAsync().ConfigureAwait(false);
        await _deferBlock.DrainAsync().ConfigureAwait(false);

        await executeWithRetriesAsync(() => _inbox.ReleaseIncomingAsync(_settings.AssignedNodeNumber, Uri)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        // Might need to drain the block
        _receiver.Complete();

        _completeBlock.Dispose();
        _deferBlock.Dispose();
    }

    public Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        envelope.Failure = exception;
        DeadLetterQueueConstants.StampFailureMetadata(envelope, exception);

        return _moveToErrors.PostAsync(envelope);
    }

    public bool NativeDeadLetterQueueEnabled => true;

    public Task MoveToScheduledUntilAsync(Envelope envelope, DateTimeOffset time)
    {
        _logger.LogDebug("Moving envelope {EnvelopeId} ({MessageType}) to scheduled status until {ScheduledTime} in durable receiver", envelope.Id, envelope.MessageType, time);
        envelope.OwnerId = TransportConstants.AnyNode;
        envelope.ScheduledTime = time;
        envelope.Status = EnvelopeStatus.Scheduled;

        return _scheduleExecution.PostAsync(envelope);
    }

    internal void SignalInboxUnavailable()
    {
        if (Interlocked.CompareExchange(ref _inboxUnavailableSignaled, 1, 0) != 0) return;

        _logger.LogWarning("Inbox database unavailable for {Uri}. Signaling listener to pause.", Uri);

        // Fire-and-forget via Task.Run to avoid deadlock:
        // We're on a RetryBlock thread; PauseForInboxRecoveryAsync drains that same RetryBlock.
        _ = Task.Run(async () =>
        {
            try
            {
                var agent = _runtime.Endpoints.FindListeningAgent(Uri);
                if (agent is ListeningAgent la)
                {
                    await la.PauseForInboxRecoveryAsync().ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error signaling listener pause for inbox recovery at {Uri}", Uri);
            }
        });
    }

    private async Task receiveOneAsync(Envelope envelope)
    {
        if (_latched)
        {
            if (!envelope.IsFromLocalDurableQueue())
            {
                // Persist once as owner id = 0, then get out.
                await executeWithRetriesAsync(async () =>
                {
                    envelope.OwnerId = TransportConstants.AnyNode;
                    assignAncillaryStoreIfNeeded(envelope);
                    try
                    {
                        await _inbox.StoreIncomingAsync(envelope).ConfigureAwait(false);
                        envelope.WasPersistedInInbox = true;
                    }
                    catch (DuplicateIncomingEnvelopeException)
                    {
                        // Just get out
                    }
                }).ConfigureAwait(false);
            }

            if (envelope.Listener != null)
            {
                try
                {
                    await envelope.Listener.DeferAsync(envelope).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error trying to defer message {MessageId} from {Listener}", envelope.Id, Uri);
                }
            }

            return;
        }

        if (ShouldPersistBeforeProcessing && !envelope.IsFromLocalDurableQueue())
        {
            try
            {
                try
                {
                    envelope.Serializer?.UnwrapEnvelopeIfNecessary(envelope);
                }
                catch (Exception e)
                {
                    _logger.LogInformation(e, "Failed to unwrap metadata for Envelope {Id} received at durable {Destination}. Moving to dead letter queue", envelope.Id, envelope.Destination);

                    if (envelope.Id == Guid.Empty)
                    {
                        envelope.Id = Envelope.IdGenerator();
                    }

                    envelope.MessageType ??= $"unknown/{e.GetType().Name}";
                    envelope.Failure = e;
                    await _moveToErrors.PostAsync(envelope).ConfigureAwait(false);
                    await _completeBlock.PostAsync(envelope).ConfigureAwait(false);
                    return;
                }

                // Have to do this before moving to the DLQ
                if (envelope.Id == Guid.Empty)
                {
                    envelope.Id = Envelope.IdGenerator();
                }

                if (envelope.MessageType.IsEmpty())
                {
                    _logger.LogInformation("Empty or missing message type name for Envelope {Id} received at durable {Destination}. Moving to dead letter queue", envelope.Id, envelope.Destination);
                    await _moveToErrors.PostAsync(envelope).ConfigureAwait(false);
                    await _completeBlock.PostAsync(envelope).ConfigureAwait(false);
                    return;
                }

                envelope.OwnerId = _settings.AssignedNodeNumber;
                assignAncillaryStoreIfNeeded(envelope);
                await _inbox.StoreIncomingAsync(envelope).ConfigureAwait(false);
                envelope.WasPersistedInInbox = true;
            }
            catch (DuplicateIncomingEnvelopeException e)
            {
                await handleDuplicateIncomingEnvelope(envelope, e).ConfigureAwait(false);

                return;
            }
            catch (Exception)
            {
                SignalInboxUnavailable();
                throw;
            }
        }

        if (envelope.Status == EnvelopeStatus.Incoming)
        {
            await EnqueueAsync(envelope).ConfigureAwait(false);
        }

        _logger.IncomingReceived(envelope, Uri);

        if (envelope.Listener != null)
        {
            await _completeBlock.PostAsync(envelope).ConfigureAwait(false);
        }
    }

    private async Task handleDuplicateIncomingEnvelope(Envelope envelope, DuplicateIncomingEnvelopeException e)
    {
        _logger.LogError(e, "Duplicate incoming envelope detected");

        if (envelope.Listener != null)
        {
            try
            {
                await envelope.Listener.CompleteAsync(envelope).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error trying to complete duplicated message {Id} from {Uri}",
                    envelope.Id, Uri);
            }
        }
    }

    private async Task executeWithRetriesAsync(Func<Task> action)
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

    // Separated for testing here.
    public async ValueTask ProcessReceivedMessagesAsync(DateTimeOffset now, IListener listener, Envelope[] envelopes)
    {
        if (_settings.Cancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException();
        }

        foreach (var envelope in envelopes) envelope.MarkReceived(listener, now, _settings, _endpoint.WireTap);

        var batchSucceeded = false;
        if (ShouldPersistBeforeProcessing)
        {
            try
            {
                assignAncillaryStoreIfNeeded(envelopes);
                await _inbox.StoreIncomingAsync(envelopes).ConfigureAwait(false);
                foreach (var envelope in envelopes)
                {
                    envelope.WasPersistedInInbox = true;
                }
                
                batchSucceeded = true;
            }
            catch (DuplicateIncomingEnvelopeException)
            {
                // The batch contained a duplicate (e.g. broker redelivery race). The inbox is fine;
                // fall through to the per-envelope path which dedupes correctly. Do NOT pause the listener.
                foreach (var envelope in envelopes) await _receivingOne.PostAsync(envelope);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to persist incoming envelopes at {Uri}", Uri);
                SignalInboxUnavailable();

                // Use finer grained retries on one envelope at a time, and this will also deal with
                // duplicate detection
                foreach (var envelope in envelopes) await _receivingOne.PostAsync(envelope).ConfigureAwait(false);
            }
        }
        else
        {
            batchSucceeded = true;
        }

        if (batchSucceeded)
        {
            foreach (var message in envelopes)
            {
                await EnqueueAsync(message).ConfigureAwait(false);
                await _completeBlock.PostAsync(message).ConfigureAwait(false);
            }
        }

        _logger.IncomingBatchReceived(Uri, envelopes);
    }

    public Task ClearInFlightIncomingAsync()
    {
        return executeWithRetriesAsync(() => _inbox.ReleaseIncomingAsync(_settings.AssignedNodeNumber, Uri));
    }

    public void Latch()
    {
        _latched = true;
    }
}
