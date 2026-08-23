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
    IAsyncDisposable, IFaultTrackingReceiver
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

    // GH-3711 (O1b): mark-as-handled was the last per-message durable write. The inbox INSERT has
    // been micro-batched since GH-3492, but every completion still issued its own UPDATE through
    // _markAsHandled -- the IReadOnlyList overload on IMessageInbox was unreachable from here. Now
    // concurrent completions share one flush (see InboxCompletionCoalescer for why the caller still
    // awaits it). Null when DurabilitySettings.MarkAsHandledBatchSize is 1, the escape hatch back to
    // one UPDATE per message.
    private readonly InboxCompletionCoalescer? _completionCoalescer;
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

        void onBlockError(Envelope? envelope, Exception ex)
        {
            // A terminal block fault (jasperfx#506) reports with a null item. Record the fault
            // FIRST, then log defensively — see BufferedReceiver.onBlockError (CritterWatch#942).
            if (envelope == null)
            {
                HasFaulted = true;
                try
                {
                    _logger.LogCritical(ex,
                        "The local worker queue for {Uri} has faulted and stopped processing. Messages buffered locally will not be executed",
                        Uri);
                }
                catch
                {
                    // Logging failed; the flag is already set and recovery does not depend on it.
                }
            }
            else
            {
                try
                {
                    _logger.LogError(ex,
                        "Error processing envelope {EnvelopeId} ({MessageType}) in the local worker queue for {Uri}",
                        envelope.Id, envelope.MessageType, Uri);
                }
                catch
                {
                    // Swallow: an escape here would fault the block terminally.
                }
            }
        }

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
                // CritterWatch#942 — everything in this recovery path must be exception-safe: an
                // escape here faults the execution block terminally (jasperfx#506) and a faulted
                // block never recovers. Both the requeue and the log call can throw under the same
                // memory pressure that caused the original failure.
                try
                {
                    if (_receiver != null)
                    {
                        await _receiver.PostAsync(envelope).ConfigureAwait(false);
                    }

                    // This *should* never happen, but of course it will
                    _logger.LogError(e, "Unexpected pipeline invocation error");
                }
                catch
                {
                    // The message is lost to this attempt, but durable inbox recovery re-offers it;
                    // the listener survives.
                }
            }
        };
        
        // GH-3867: an endpoint that executes assembled message batches is its own cascade target,
        // and a bounded block closes a deadlock cycle through the batching channel. See
        // Endpoint.HostsBatchExecution.
        var boundedCapacity = endpoint.HostsBatchExecution
            ? Block<Envelope>.Unbounded
            : Block<Envelope>.DefaultBoundedCapacity;

        if (endpoint.GroupShardingSlotNumber == null)
        {
            _receiver = new Block<Envelope>(endpoint.MaxDegreeOfParallelism, boundedCapacity, execute);
        }
        else
        {
            var sharded = new ShardedExecutionBlock((int)endpoint.GroupShardingSlotNumber,
                runtime.Options.MessagePartitioning, boundedCapacity, execute,
                // GH-3899: message types exempted from partitioned processing run at the
                // endpoint's normal parallelism instead of a sequential GroupId slot
                endpoint.MaxDegreeOfParallelism);
            sharded.OnError = onBlockError;
            _receiver = sharded.DeserializeFirst(pipeline, runtime, this);
        }

        // Route block-level failures (an exception escaping the execution machinery itself, or the
        // block faulting terminally per jasperfx#506) through real logging. The JasperFx default sink
        // is stderr, which reads as a silent stall in any structured-logging deployment — and a
        // faulted block freezes QueueCount, which permanently latches a back-pressured listener
        // (GH CritterWatch#922).
        _receiver.OnError = onBlockError;
        
        _deferBlock = new RetryBlock<Envelope>((env, _) => env.Listener!.DeferAsync(env).AsTask(), runtime.Logger,
            runtime.Cancellation);
        // GH-4012: the outer half of the shared settle budget. This block only CHECKS -- the
        // increment happens at the innermost site that actually issues the broker call, because
        // that is the only layer that knows a round trip really occurred. For a transport whose
        // listener settles directly (no inner retry block), nothing increments and this guard never
        // trips, leaving that transport's behavior exactly as it was.
        var maximumAckAttempts = runtime.DurabilitySettings.MaximumAckAttempts;
        _completeBlock = new RetryBlock<Envelope>(async (env, _) =>
        {
            if (env.AckAttempts >= maximumAckAttempts)
            {
                // Swallow rather than throw: retrying here would only re-enter the inner block,
                // which has already spent the budget. Leaving the delivery unsettled is the
                // recoverable outcome -- the broker redelivers and the inbox deduplicates.
                _logger.LogWarning(
                    "Giving up on settling envelope {EnvelopeId} at {Uri} after {AckAttempts} attempts; leaving it for broker redelivery",
                    env.Id, Uri, env.AckAttempts);
                return;
            }

            await env.Listener!.CompleteAsync(env).ConfigureAwait(false);
        }, runtime.Logger, runtime.Cancellation);


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

        if (_settings.MarkAsHandledBatchSize > 1)
        {
            _completionCoalescer = new InboxCompletionCoalescer(
                envelopes => _inbox.MarkIncomingEnvelopeAsHandledAsync(envelopes),
                envelope => _markAsHandled.PostAsync(envelope),
                _settings.MarkAsHandledBatchSize, Uri, _logger);
        }

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
    /// persists it in the correct store for transactional atomicity. The
    /// receiving handler's store association wins over the publishing context's
    /// store — see the equivalent method on
    /// <see cref="Wolverine.Transports.Local.DurableLocalQueue"/> for the full
    /// rationale (GH-2669).
    /// </summary>
    private void assignAncillaryStoreIfNeeded(Envelope envelope)
    {
        if (_runtime.Stores == null) return;

        // Uri, not just the message type: a sticky handler makes the owning store endpoint specific,
        // and this receiver only ever feeds the handler chain for its own endpoint (GH-3886).
        var store = _runtime.Stores.TryFindAncillaryStoreForMessageType(Uri, envelope.MessageType);
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
            // CritterWatch#942 — same settlement as BufferedReceiver: the members' pending-count
            // against their originating listeners drains with the batch's terminal.
            if (_runtime is WolverineRuntime runtime)
            {
                runtime.BatchingPendingCounts.SettleBatch(envelope);
            }

            // GH-3711: post every child before awaiting any, so the coalescer can take them as one flush
            var completions = new List<Task>(envelope.Batch.Length);
            foreach (var child in envelope.Batch)
            {
                child.InBatch = false;
                completions.Add(markAsHandledAsync(child).AsTask());
            }

            await Task.WhenAll(completions).ConfigureAwait(false);
        }
        else
        {
            await markAsHandledAsync(envelope).ConfigureAwait(false);
        }
    }

    private async ValueTask markAsHandledAsync(Envelope envelope)
    {
        // Same optimization as the per-envelope block: transactional middleware may already have
        // marked the envelope handled inside the handler's own transaction
        if (envelope.Status == EnvelopeStatus.Handled)
        {
            return;
        }

        if (_completionCoalescer != null)
        {
            await _completionCoalescer.MarkAsHandledAsync(envelope).ConfigureAwait(false);
            return;
        }

        await _markAsHandled.PostAsync(envelope).ConfigureAwait(false);
    }

    /// <summary>
    ///     GH-3711: let any flush in flight finish before the per-envelope block drains, so the
    ///     fallback path still has somewhere to go.
    /// </summary>
    private async Task flushMarkAsHandledBatchAsync()
    {
        if (_completionCoalescer == null)
        {
            return;
        }

        try
        {
            await _completionCoalescer.DrainAsync().WaitAsync(_settings.DrainTimeout).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Error waiting for pending mark-as-handled batches at {Uri}", Uri);
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

    /// <summary>CritterWatch#942 — set when the execution block faults terminally (jasperfx#506).</summary>
    public bool HasFaulted { get; private set; }

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
        await flushMarkAsHandledBatchAsync().ConfigureAwait(false);
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

                    // GH-3680 defense in depth. Never write an inbox row that no recovery sweep can see.
                    // Status defaults to 'Outgoing' and received_at to null on an envelope that never went
                    // through MarkReceived, and both are filter columns for inbox recovery.
                    if (envelope.Status == EnvelopeStatus.Outgoing)
                    {
                        envelope.Status = EnvelopeStatus.Incoming;
                    }

                    envelope.Destination ??= Uri;

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

                if (envelope.Listener == null)
                {
                    // Nothing to settle with the broker, so let the RetryBlock keep trying
                    throw;
                }

                // GH-3767. RetryBlock exhaustion (3 attempts over ~400ms) must never be the
                // terminal state for a broker delivery that has not been settled: discarding
                // leaves the delivery unacked on a live consumer, and the broker will not
                // redeliver it while the connection stays up. Settle by deferring back to the
                // listener -- the same semantics the latched branch applies -- and let broker
                // redelivery plus the paused listener's inbox-recovery restart carry the retry.
                try
                {
                    await envelope.Listener.DeferAsync(envelope).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _logger.LogError(e,
                        "Error trying to defer message {MessageId} from {Listener} after an inbox persistence failure",
                        envelope.Id, Uri);
                }

                return;
            }
        }

        // Settle the broker delivery BEFORE enqueueing for execution. The envelope is durable the moment
        // the inbox write committed (or, for a database-backed endpoint, already was), and the worker
        // queue is bounded: when it is full EnqueueAsync blocks, and acking after it meant a persisted
        // message sat un-acked behind the queue. On a back-pressure stop every one of those was then
        // redelivered by the broker -- for JetStream only after AckWait, and counted against
        // MaxAckPending the whole time, so the restarted consumer was starved for 30s and then hit
        // ~1,000 duplicate inserts in a row. See GH-4026.
        if (envelope.Listener != null)
        {
            await _completeBlock.PostAsync(envelope).ConfigureAwait(false);
        }

        if (envelope.Status == EnvelopeStatus.Incoming)
        {
            await EnqueueAsync(envelope).ConfigureAwait(false);
        }

        _logger.IncomingReceived(envelope, Uri);
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

    /// <summary>
    /// Bounded retry helper used for best-effort persistence operations like
    /// <see cref="IMessageInbox.ReleaseIncomingAsync"/> on drain. Three properties
    /// matter for shutdown correctness (see GH-2671):
    /// <list type="bullet">
    /// <item>The loop is finite — capped at <see cref="MaxReleaseRetries"/> attempts —
    /// so a permanently unreachable database can't hang shutdown.</item>
    /// <item>The loop honours <see cref="DurabilitySettings.Cancellation"/>: when the
    /// host is stopping we exit immediately on the first failure rather than
    /// hammering an already-disposed connection pool.</item>
    /// <item>Log severity is demoted to Debug when cancellation has been signalled.
    /// During teardown, transient socket / connection failures from the data
    /// source are expected and don't warrant Error-level noise.</item>
    /// </list>
    /// </summary>
    internal const int MaxReleaseRetries = 5;

    private async Task executeWithRetriesAsync(Func<Task> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await action().ConfigureAwait(false);
                return;
            }
            catch (Exception e)
            {
                // Shutdown-aware exit: when the cancellation token has been signalled
                // we treat any failure as terminal and demote the log level. Retrying
                // here is futile (the DataSource is being torn down) and the inbox
                // ownership we failed to release will be reclaimed by the durability
                // agent on the next live node.
                if (_settings.Cancellation.IsCancellationRequested)
                {
                    _logger.LogDebug(e,
                        "Database operation failed during shutdown at {Uri}; exiting retry loop",
                        Uri);
                    return;
                }

                if (attempt >= MaxReleaseRetries)
                {
                    _logger.LogError(e,
                        "Database operation at {Uri} failed after {Attempts} attempts; giving up",
                        Uri, attempt);
                    return;
                }

                _logger.LogError(e,
                    "Unexpected failure at {Uri} (attempt {Attempt}/{Max})",
                    Uri, attempt, MaxReleaseRetries);

                try
                {
                    await Task.Delay(attempt * 100, _settings.Cancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Cancellation fired while we were backing off — exit cleanly
                    // instead of throwing out of a best-effort cleanup path.
                    return;
                }
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

        // GH-3680. MarkReceived has to happen BEFORE the latch check, not after it. It is what stamps
        // Status = Incoming, Destination (persisted as received_at) and Listener onto the envelope. The
        // latched branch below persists the envelope to the inbox as a safety net, so skipping MarkReceived
        // wrote rows with the default Status of 'Outgoing' and a null received_at -- invisible to
        // CheckRecoverableIncomingMessagesOperation (which filters on Status = 'Incoming') and to
        // LoadPageOfGloballyOwnedIncomingAsync (which filters on received_at), i.e. permanently orphaned.
        // A null Listener also meant the latched branch silently skipped the defer back to the broker, so
        // nothing else was going to redeliver them either. That is real message loss under a *durable*
        // inbox whenever a circuit breaker trip latches the receiver mid-flight.
        foreach (var envelope in envelopes) envelope.MarkReceived(listener, now, _settings, _endpoint.WireTap);

        // A latched receiver must apply the full single-envelope semantics (persist as
        // owner 0 + defer back to the listener), so route through the one-at-a-time path
        // which already implements them (GH-3492).
        if (_latched)
        {
            foreach (var envelope in envelopes) await _receivingOne.PostAsync(envelope).ConfigureAwait(false);
            return;
        }

        // Per-envelope guards that the single-envelope path (receiveOneAsync) has always
        // applied but this batch path historically skipped (GH-3492): serializer unwrap
        // (MassTransit-style interop), missing id/message-type dead-lettering, and expiry.
        // Envelopes that fail a guard are handled individually and drop out of the batch.
        var survivors = new List<Envelope>(envelopes.Length);
        foreach (var envelope in envelopes)
        {
            if (ShouldPersistBeforeProcessing && !envelope.IsFromLocalDurableQueue())
            {
                try
                {
                    envelope.Serializer?.UnwrapEnvelopeIfNecessary(envelope);
                }
                catch (Exception e)
                {
                    _logger.LogInformation(e,
                        "Failed to unwrap metadata for Envelope {Id} received at durable {Destination}. Moving to dead letter queue",
                        envelope.Id, envelope.Destination);

                    if (envelope.Id == Guid.Empty)
                    {
                        envelope.Id = Envelope.IdGenerator();
                    }

                    envelope.MessageType ??= $"unknown/{e.GetType().Name}";
                    envelope.Failure = e;
                    await _moveToErrors.PostAsync(envelope).ConfigureAwait(false);
                    await _completeBlock.PostAsync(envelope).ConfigureAwait(false);
                    continue;
                }

                if (envelope.Id == Guid.Empty)
                {
                    envelope.Id = Envelope.IdGenerator();
                }

                if (envelope.MessageType.IsEmpty())
                {
                    _logger.LogInformation(
                        "Empty or missing message type name for Envelope {Id} received at durable {Destination}. Moving to dead letter queue",
                        envelope.Id, envelope.Destination);
                    await _moveToErrors.PostAsync(envelope).ConfigureAwait(false);
                    await _completeBlock.PostAsync(envelope).ConfigureAwait(false);
                    continue;
                }
            }

            if (envelope.IsExpired())
            {
                await _completeBlock.PostAsync(envelope).ConfigureAwait(false);
                continue;
            }

            survivors.Add(envelope);
        }

        if (survivors.Count == 0)
        {
            return;
        }

        if (survivors.Count != envelopes.Length)
        {
            envelopes = survivors.ToArray();
        }

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
                // The batch contained at least one duplicate. We cannot trust which
                // envelopes were actually persisted (some drivers autocommit per
                // statement on multi-statement batches), so we re-attempt every
                // envelope through the per-envelope path. The single-envelope
                // StoreIncomingAsync correctly distinguishes fresh inserts from
                // duplicates: fresh ones get persisted and pipelined, duplicates
                // throw and are completed at the listener via
                // handleDuplicateIncomingEnvelope. Do NOT pause the listener.
                foreach (var envelope in envelopes) await _receivingOne.PostAsync(envelope).ConfigureAwait(false);
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
            // Settle the whole batch with the broker first, then enqueue -- see receiveOneAsync for why.
            // Acking per envelope AFTER EnqueueAsync meant that once the bounded worker queue filled, the
            // rest of an already-persisted batch sat un-acked, and a back-pressure stop turned all of it
            // into redeliveries and duplicate inserts.
            foreach (var message in envelopes)
            {
                await _completeBlock.PostAsync(message).ConfigureAwait(false);
            }

            foreach (var message in envelopes)
            {
                await EnqueueAsync(message).ConfigureAwait(false);
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
