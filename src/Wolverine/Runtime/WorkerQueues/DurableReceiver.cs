using System.Collections.Concurrent;
using JasperFx.Blocks;
using JasperFx.Core;
using MassTransit;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Partitioning;
using Wolverine.Runtime.Scheduled;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Runtime.WorkerQueues;

public class DurableReceiver : ILocalQueue, IChannelCallback, ISupportNativeScheduling, ISupportDeadLetterQueue,
    IAsyncDisposable, IFaultTrackingReceiver, ILatchedReceiver
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
    private readonly RetryBlock<Envelope> _rescheduleAfterFailedDeadLetter;
    private readonly IBlock<Envelope> _receiver;
    private readonly RetryBlock<Envelope> _receivingOne;
    private readonly IWolverineRuntime _runtime;
    private readonly RetryBlock<Envelope> _scheduleExecution;

    // GH-5120. A batch carrier has no inbox row of its own -- only its members do -- so its scheduled retry
    // is held in memory, the same way BufferedReceiver schedules everything. See MoveToScheduledUntilAsync.
    private readonly InMemoryScheduledJobProcessor _batchRetries;

    // F4. The carriers waiting in _batchRetries, so a drain that cancels them can hand their members back
    private readonly ConcurrentDictionary<Guid, Envelope> _pendingBatchRetries = new();
    private readonly DurabilitySettings _settings;

    // These members are for draining
    private bool _latched;
    private int _inboxUnavailableSignaled;

    // F15/F16. Set when DrainAsync starts: nothing posted after this point will execute here
    private volatile bool _draining;

    // Posts in progress. A post that read _draining as false before the drain set it must reach the block before the
    // drain completes it: a completed block drops a post without a word, which would lose the envelope in memory
    // and strand its row. See waitForPostsInProgressAsync.
    private int _posting;

    // F15/F16. Set once anything has been received or enqueued, so a receiver that was built and never used
    // (a listener's receiver replaced by a partition bridge) does not count as working at its address
    private volatile bool _activated;

    // F15/F16. Set once the drain's release has run. Anything abandoned after that is released on its own
    private volatile bool _released;

    // F15/F16. Envelopes this receiver took ownership of and will never execute: skipped by the latch, deferred
    // while latched, posted after the drain started, or members of a batch whose retry the drain cancelled. When
    // the drain cannot use the blanket release, because another receiver at the same address is still working,
    // exactly these rows are handed back. See releaseAfterDrainAsync.
    private readonly ConcurrentQueue<Envelope> _abandoned = new();

    // The envelopes this receiver holds in memory. With InboxStaleTime set (and an inbox that supports the
    // heartbeat) that is everything queued or running here, and the timer restarts their rows' stale clock.
    // Otherwise only batch members waiting on their batch are tracked, and there is no timer.
    private readonly InboxCustody _custody;
    private readonly Timer? _heartbeat;
    private readonly TimeSpan _heartbeatPeriod;
    private int _heartbeating;

    // F18. The heartbeat runs only while something is held: started by the first envelope taken into custody, and
    // stopped by the first tick that finds custody empty. Guarded by _heartbeatLock, read without it on the hot path.
    private readonly object _heartbeatLock = new();
    private int _heartbeatRunning;
    private bool _heartbeatDisposed;

    // F18. Set by Dispose/DisposeAsync: the timer is disposed for good once custody is empty, rather than stopped
    private volatile bool _disposed;

    public DurableReceiver(Endpoint endpoint, IWolverineRuntime runtime, IHandlerPipeline pipeline)
    {
        _endpoint = endpoint;
        _runtime = runtime;
        _settings = runtime.DurabilitySettings;
        
        // the check for Stores being null is honestly just because of some tests that use a little too much mocking
        _inbox = runtime .Stores != null && runtime.Stores.HasAnyAncillaryStores() ? new DelegatingMessageInbox(runtime.Storage.Inbox, runtime.Stores) : runtime.Storage.Inbox;
        _logger = runtime.LoggerFactory.CreateLogger<DurableReceiver>();

        Uri = endpoint.Uri;

        // The stale sweep releases an owned inbox row once its clock is older than InboxStaleTime. A row this
        // receiver is still holding -- queued here, or running in a handler -- is not stuck, so its clock is
        // restarted three times per InboxStaleTime while it is held.
        var heartbeat = _settings.InboxStaleTime.HasValue && _inbox is IMessageInboxHeartbeat;
        _custody = new InboxCustody(heartbeat);
        if (heartbeat && _settings.InboxStaleTime is { } staleTime)
        {
            _heartbeatPeriod = TimeSpan.FromTicks(Math.Max(staleTime.Ticks / 3, TimeSpan.FromMilliseconds(50).Ticks));
            _heartbeat = new Timer(_ => _ = restartHeldStaleClocksAsync(), null, Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
        }

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
            var heldForBatch = false;
            try
            {
                if (_latched)
                {
                    // Owned here and never going to run here: handed back by the drain's release
                    abandon(envelope);
                    return;
                }

                try
                {
                    envelope.ContentType ??= EnvelopeConstants.JsonContentType;

                    var wasInBatch = envelope.InBatch;
                    await pipeline.InvokeAsync(envelope, this).ConfigureAwait(false);

                    // F4/F5. The batching pipeline accepted this envelope as a batch member. Its execution ends
                    // here, but its row stays Incoming and owned at this address until the batch finishes with it,
                    // so it stays in this receiver's custody until then -- see InboxCustody
                    heldForBatch = !wasInBatch && envelope.InBatch && envelope.Batch == null;
                }
                catch (Exception? e)
                {
                    // CritterWatch#942 — everything in this recovery path must be exception-safe: an
                    // escape here faults the execution block terminally (jasperfx#506) and a faulted
                    // block never recovers. Both the requeue and the log call can throw under the same
                    // memory pressure that caused the original failure.
                    try
                    {
                        if (_draining)
                        {
                            // A drained block silently drops a post, which would strand the row
                            abandon(envelope);
                        }
                        else if (_receiver != null)
                        {
                            // Still held: the requeue is counted in before this execution counts out below
                            enterCustody(envelope);
                            try
                            {
                                await _receiver.PostAsync(envelope).ConfigureAwait(false);
                            }
                            catch
                            {
                                // The requeue never reached the block, so it never counts out on its own
                                _custody.Leave(envelope);
                                throw;
                            }
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
            }
            finally
            {
                if (heldForBatch)
                {
                    _custody.HoldForBatch(envelope, DateTimeOffset.UtcNow);
                }
                else
                {
                    _custody.Leave(envelope);
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

        // GH-4664. This block's default budget was 3 retries over ~400ms, and exhausting it discarded the
        // completion with one line at Information. The envelope's inbox row then stayed Incoming and owned
        // by THIS node, which is the one state nothing reclaims: the recovery sweep reads owner_id = 0
        // only, and the dead-node sweep deliberately never touches a live node's rows (GH-3850). So a
        // database blip during a handler's last millisecond stranded that message until the node next
        // restarted -- days, in a long-running service -- and DurabilitySettings.InboxStaleTime, whose own
        // documentation says it "should NOT ever be necessary", was the only way back.
        //
        // Unlike the first write of an envelope (GH-4662), there is nothing to hand back to a caller here
        // and nothing to lose by waiting: the row is already durable, this node still owns it so no other
        // node can pick it up twice, and the handler has already run. So keep retrying, backing off to the
        // same 30s ceiling InboxHealthRestarter uses, until the store comes back or the host shuts down --
        // at which point DrainAsync releases the ownership anyway. The cost is holding the envelope in
        // memory for the length of the outage.
        _markAsHandled.MaximumAttempts = int.MaxValue;
        _markAsHandled.Pauses =
        [
            50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds(), 1.Seconds(), 2.Seconds(), 5.Seconds(),
            10.Seconds(), 30.Seconds()
        ];

        if (_settings.MarkAsHandledBatchSize > 1)
        {
            _completionCoalescer = new InboxCompletionCoalescer(
                envelopes => _inbox.MarkIncomingEnvelopeAsHandledAsync(envelopes),
                envelope => _markAsHandled.PostAsync(envelope),
                _settings.MarkAsHandledBatchSize, Uri, _logger);
        }

        _incrementAttempts = new RetryBlock<Envelope>((e, _) => _inbox.IncrementIncomingEnvelopeAttemptsAsync(e),
            _logger, _settings.Cancellation);

        // F12. The same GH-4664 reasoning as mark-as-handled above. A failure handed to ScheduleRetry, or a
        // RetryWithCooldown that ends in one, writes the row back as Scheduled for any node. With the default
        // budget of 3 retries over ~400ms, a longer database blip discarded that write and left the row Incoming
        // and owned by this live node, which nothing reclaims. The row is durable and still owned here, so keep
        // trying until the store comes back or the host shuts down.
        _scheduleExecution = new RetryBlock<Envelope>((e, _) => scheduleExecutionAsync(e), _logger,
            _settings.Cancellation)
        {
            MaximumAttempts = int.MaxValue,
            Pauses =
            [
                50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds(), 1.Seconds(), 2.Seconds(), 5.Seconds(),
                10.Seconds(), 30.Seconds()
            ]
        };

        // GH-5105. A dead-letter write that could not be made hands the inbox row back to be executed again. The
        // row is durable and still owned by this node, so -- as with mark-as-handled (GH-4664) -- keep trying
        // until the store comes back or the host shuts down, at which point DrainAsync releases the ownership
        _rescheduleAfterFailedDeadLetter = new RetryBlock<Envelope>((e, _) => scheduleExecutionAsync(e), _logger,
            _settings.Cancellation)
        {
            MaximumAttempts = int.MaxValue,
            Pauses =
            [
                50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds(), 1.Seconds(), 2.Seconds(), 5.Seconds(),
                10.Seconds(), 30.Seconds()
            ]
        };

        _receivingOne = new RetryBlock<Envelope>((e, _) => receiveOneAsync(e), _logger, _settings.Cancellation);

        _batchRetries = new InMemoryScheduledJobProcessor(this, _logger);

        if (endpoint.TryBuildDeadLetterSender(runtime, out var dlq))
        {
            _deadLetterSender = dlq;
        }

        DurableReceiverRegistry.Register(runtime, this);
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

        // GH-5040: a database-backed endpoint never writes the inbox itself -- its dequeue already moved the row
        // into the inbox of the transport's own store. Pointing the envelope at the handler's ancillary store
        // here sent the mark-handled to a database that has no such row, and the row stayed Incoming in the
        // main store to be replayed on every restart
        if (!ShouldPersistBeforeProcessing) return;

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

    /// <summary>
    /// One heartbeat tick: restart the stale clock of every envelope held here for at least one period. Never
    /// overlaps itself and never throws; a failed write is retried on the next tick, which still leaves two more
    /// before the sweep could release the row.
    /// </summary>
    private async Task restartHeldStaleClocksAsync()
    {
        if (_heartbeat == null || Interlocked.Exchange(ref _heartbeating, 1) == 1) return;

        try
        {
            var now = DateTimeOffset.UtcNow;
            var due = _custody.FindDue(now.Subtract(_heartbeatPeriod));
            if (due.Count == 0) return;

            await ((IMessageInboxHeartbeat)_inbox).RestartStaleClockAsync(_settings.AssignedNodeNumber, due)
                .ConfigureAwait(false);
            _custody.MarkClockRestarted(due, now);
        }
        catch (Exception e)
        {
            try
            {
                _logger.LogWarning(e,
                    "Unable to restart the InboxStaleTime clock of messages held by {Uri}. Retrying on the next heartbeat",
                    Uri);
            }
            catch
            {
                // Logging failed; the next tick tries again regardless
            }
        }
        finally
        {
            Volatile.Write(ref _heartbeating, 0);
            stopHeartbeatIfIdle();
        }
    }

    /// <summary>
    /// Take <paramref name="envelope" /> into custody, and make sure the heartbeat is running for it
    /// </summary>
    private void enterCustody(Envelope envelope)
    {
        _custody.Enter(envelope, DateTimeOffset.UtcNow);
        startHeartbeatIfStopped();
    }

    private void startHeartbeatIfStopped()
    {
        if (_heartbeat == null || Volatile.Read(ref _heartbeatRunning) == 1) return;

        lock (_heartbeatLock)
        {
            if (_heartbeatDisposed || _heartbeatRunning == 1) return;

            Volatile.Write(ref _heartbeatRunning, 1);
            _heartbeat.Change(_heartbeatPeriod, _heartbeatPeriod);
        }
    }

    /// <summary>
    /// F18. Stop the heartbeat once custody is empty, and dispose it for good once the receiver is disposed too or the
    /// host is stopping. An envelope taken into custody concurrently either sees the heartbeat stopped and starts it
    /// again, or is seen here and keeps it running.
    /// </summary>
    private void stopHeartbeatIfIdle()
    {
        if (_heartbeat == null) return;

        lock (_heartbeatLock)
        {
            if (_heartbeatDisposed) return;

            var wasRunning = Volatile.Read(ref _heartbeatRunning) == 1;
            Volatile.Write(ref _heartbeatRunning, 0);
            Interlocked.MemoryBarrier();

            var stopping = _settings.Cancellation.IsCancellationRequested;
            if (!stopping && !_custody.IsEmpty)
            {
                // Still holding something: batch members waiting on a batch above all, even after a dispose
                Volatile.Write(ref _heartbeatRunning, 1);
                if (!wasRunning) _heartbeat.Change(_heartbeatPeriod, _heartbeatPeriod);
                return;
            }

            if (_disposed || stopping)
            {
                _heartbeatDisposed = true;
                _heartbeat.Dispose();
            }
            else
            {
                _heartbeat.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>
    /// Test seam: is the InboxStaleTime heartbeat timer scheduled?
    /// </summary>
    internal bool HeartbeatIsRunning
    {
        get
        {
            if (_heartbeat == null) return false;
            lock (_heartbeatLock)
            {
                return !_heartbeatDisposed && _heartbeatRunning == 1;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _draining = true;
        _batchRetries.Dispose();

        await _receiver.WaitForCompletionAsync().ConfigureAwait(false);

        // F18. Batch members that arrived here stay in custody, with their heartbeat, until their batch settles them
        _disposed = true;
        stopHeartbeatIfIdle();

        _incrementAttempts.Dispose();
        _scheduleExecution.Dispose();
        _markAsHandled.Dispose();
        _rescheduleAfterFailedDeadLetter.Dispose();
        _receivingOne.Dispose();

        if (_deadLetterSender is IDisposable d)
        {
            d.SafeDispose();
        }

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

            // F4/F5. The batch is finished with its members, so the receivers they arrived on stop holding them
            InboxCustody.SettleBatchMembers(envelope);
        }
        else
        {
            await markAsHandledAsync(envelope).ConfigureAwait(false);

            // A batch member replayed on its own (item isolation) is finished with here
            InboxCustody.TrySettleBatchMember(envelope);
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
        // GH-4851. Deliberately NOT incrementing Attempts here, for any envelope. Every execution goes through
        // Executor.ExecuteAsync (or TracingExecutor), which increments Attempts as it starts, so the requeued
        // execution counts itself. GH-826 removed the second increment for envelopes sent by a DurableLocalQueue
        // only, which left every external durable listener -- a database queue, a broker -- reporting attempts
        // 1 then 3 on a Requeue, and firing attempt-bounded policies one try early. The persisted value below
        // is the attempt that just failed; a recovered row resumes at the next one, which is the same number
        // an in-memory requeue would reach.
        await _incrementAttempts.PostAsync(envelope).ConfigureAwait(false);

        if (_latched)
        {
            if (envelope.Listener != null)
            {
                await _deferBlock.PostAsync(envelope).ConfigureAwait(false);
            }

            // The row is still owned here and nothing here will run it again, so the drain's release hands it
            // back -- the blanket release always did, and abandon() keeps that true when the drain cannot use it
            abandon(envelope);
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

        // A batch retry coming due
        if (envelope.Batch != null) _pendingBatchRetries.TryRemove(envelope.Id, out _);

        Interlocked.Increment(ref _posting);
        try
        {
            if (_draining)
            {
                // A drained block drops a post without a word, which would strand the row owned by this node
                abandon(envelope);
                return;
            }

            // The rows behind a synchronous enqueue were claimed before they got here, so there is nothing to wait for
            if (!_activated) _ = ActivateAsync();

            enterCustody(envelope);
            try
            {
                _receiver.Post(envelope);
            }
            catch
            {
                _custody.Leave(envelope);
                throw;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _posting);
        }
    }

    public ValueTask EnqueueAsync(Envelope envelope)
    {
        if (!_activated) return activateThenEnqueueAsync(envelope);

        envelope.WasPersistedInInbox = true;
        envelope.ReplyUri = envelope.ReplyUri ?? Uri;
        // See note on Enqueue — same reason. The scheduled-jobs poller in
        // {DatabaseFlavour}MessageStore.PollForScheduledMessagesAsync calls
        // runtime.EnqueueDirectlyAsync, which lands here without ever passing
        // through the assignAncillaryStoreIfNeeded calls in receiveOneAsync /
        // ProcessReceivedMessagesAsync. See GH-2576.
        assignAncillaryStoreIfNeeded(envelope);

        Interlocked.Increment(ref _posting);
        if (_draining)
        {
            Interlocked.Decrement(ref _posting);

            // A drained block drops a post without a word, which would strand the row owned by this node. Handed
            // back instead, through its row: the drain's release (or a release by id, if that already ran) gives
            // it to any node.
            abandon(envelope);
            return ValueTask.CompletedTask;
        }

        enterCustody(envelope);
        return postAsync(envelope);
    }

    private async ValueTask activateThenEnqueueAsync(Envelope envelope)
    {
        await ActivateAsync().ConfigureAwait(false);
        await EnqueueAsync(envelope).ConfigureAwait(false);
    }

    private async ValueTask postAsync(Envelope envelope)
    {
        try
        {
            await _receiver.PostAsync(envelope).ConfigureAwait(false);
        }
        catch
        {
            _custody.Leave(envelope);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _posting);
        }
    }

    /// <summary>
    /// Let a post that started before the drain did reach the block before it is completed, so the latch skips it
    /// (and abandons it) rather than the completed block dropping it. Bounded: a bounded block can make a post wait
    /// for room, and the frame waiting here may be the only one that would make it.
    /// </summary>
    private async Task waitForPostsInProgressAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(1);
        while (Volatile.Read(ref _posting) > 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    public ValueTask ReceivedAsync(IListener listener, Envelope[] messages)
    {
        if (!_activated) return activateThenReceiveAsync(listener, messages);

        var now = DateTimeOffset.UtcNow;

        return ProcessReceivedMessagesAsync(now, listener, messages);
    }

    private async ValueTask activateThenReceiveAsync(IListener listener, Envelope[] messages)
    {
        await ActivateAsync().ConfigureAwait(false);
        await ProcessReceivedMessagesAsync(DateTimeOffset.UtcNow, listener, messages).ConfigureAwait(false);
    }

    /// <summary>
    /// Count as working at this receiver's addresses from now on, so a drained receiver at the same address hands
    /// back only the rows it abandoned rather than every row this node owns there -- and first wait out any such
    /// blanket release already in flight, so nothing claimed for this receiver afterwards can be caught by it. Called
    /// by a listener (or a resumed durable local queue) before it can claim anything for this receiver, and on the
    /// first receipt otherwise. See <see cref="DurableReceiverRegistry" />.
    /// </summary>
    internal Task ActivateAsync()
    {
        return DurableReceiverRegistry.ActivateAsync(_runtime, this, () => _activated = true);
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

        if (!_activated) await ActivateAsync().ConfigureAwait(false);

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
        _draining = true;

        // GH-5120. A batch retry still pending must not fire after the release below hands its members' rows to
        // another node, or both would execute them. Cancelled, the members are recovered from their rows -- but
        // only if something hands those rows back. F4: they are owned at the address each member ARRIVED on, which
        // is usually not this one, so the blanket release below never touched them and they sat Incoming and owned
        // by this live node. They are abandoned instead, which releases them by id.
        var cancelled = _pendingBatchRetries.Values.ToArray();
        _pendingBatchRetries.Clear();
        await _batchRetries.EmptyAllAsync().ConfigureAwait(false);
        foreach (var carrier in cancelled)
        {
            abandon(carrier);
        }

        await waitForPostsInProgressAsync().ConfigureAwait(false);
        _receiver.Complete();

        // GH-4797. Non-null only when the bounded wait below gave up with work still running, and it is then
        // the thing the release has to wait for. See deferReleaseUntilIdleAsync.
        Task? stillRunning = null;

        if (waitForCompletion)
        {
            try
            {
                var completion = _receiver.WaitForCompletionAsync();
                var timeout = Task.Delay(_settings.DrainTimeout);

                // GH-4797. Which task won is load-bearing, and Task.WhenAny discards it. The blanket
                // ReleaseIncomingAsync at the end of this method hands every row at this address back to
                // AnyNode, and running it while handlers are still executing lets another node claim and
                // re-execute a message this node has not finished -- for an exclusive listener, and especially
                // a global partition slot handing over (GH-4777), exactly the intra-group concurrency the
                // partitioned modes exist to prevent.
                if (await Task.WhenAny(completion, timeout).ConfigureAwait(false) == timeout)
                {
                    stillRunning = completion;

                    _logger.LogWarning(
                        "Drain at {Uri} timed out after {DrainTimeout} with {QueueCount} message(s) still queued or in flight. Their inbox rows will be held until the work finishes rather than released now, so nothing else can claim a message still executing here. See GH-4797",
                        Uri, _settings.DrainTimeout, QueueCount);
                }
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "Error waiting for in-flight message processing to complete at {Uri}", Uri);
            }
        }
        else
        {
            // F15. An unlatched drain -- a pause raised from inside the handler pipeline -- cannot wait here for
            // the work still running, because the execution that raised it is on this call stack. It used to
            // release straight away instead, which handed every sibling handler's row to AnyNode while it was still
            // running, and another node ran it alongside. Same answer as GH-4797: hold the release until the work
            // finishes, on a continuation rather than on this frame.
            var completion = _receiver.WaitForCompletionAsync();
            if (!completion.IsCompleted)
            {
                stillRunning = completion;
            }
        }

        await _incrementAttempts.DrainAsync().ConfigureAwait(false);
        await _scheduleExecution.DrainAsync().ConfigureAwait(false);
        await flushMarkAsHandledBatchAsync().ConfigureAwait(false);
        await _markAsHandled.DrainAsync().ConfigureAwait(false);
        await _rescheduleAfterFailedDeadLetter.DrainAsync().ConfigureAwait(false);
        await _receivingOne.DrainAsync().ConfigureAwait(false);

        await _completeBlock.DrainAsync().ConfigureAwait(false);
        await _deferBlock.DrainAsync().ConfigureAwait(false);

        if (stillRunning == null)
        {
            await releaseAfterDrainAsync().ConfigureAwait(false);
            return;
        }

        deferReleaseUntilIdleAsync(stillRunning);
    }

    /// <summary>
    /// Is this receiver still working on rows at its address: executing or queued, holding batch members for a
    /// batch that has not finished with them, or simply live and receiving? Used by another receiver at the same
    /// address before it hands that address's rows back. See <see cref="DurableReceiverRegistry" />.
    /// </summary>
    internal bool HoldsWork => _receiver.Count > 0 || _custody.BatchMemberCount > 0 || (_activated && !_draining);

    /// <summary>
    /// Drained, and nothing left that another receiver needs to know about
    /// </summary>
    internal bool IsRetired => _draining && !HoldsWork;

    /// <summary>
    /// How many batch members that arrived here are still waiting on a batch that has not finished with them
    /// </summary>
    internal int HeldBatchMemberCount => _custody.BatchMemberCount;

    /// <summary>
    /// Whether this receiver restarts the InboxStaleTime clock of the rows it holds
    /// </summary>
    internal bool HasInboxHeartbeat => _heartbeat != null;

    internal bool AnswersTo(Uri address) => address == Uri || address == AlsoReleaseIncomingAt;

    private IReadOnlyList<Uri> releaseAddresses() =>
        AlsoReleaseIncomingAt == null ? [Uri] : [Uri, AlsoReleaseIncomingAt];

    /// <summary>
    /// The drain's release, once nothing this receiver was running is still running. The blanket release by
    /// address is only safe when nothing else is still working on rows at that address: it hands back EVERY row
    /// this node owns there. F16: after a pause or a restart the old receiver is often still finishing a handler
    /// when the new one starts receiving, and holding batch members means some rows are waiting on a batch
    /// elsewhere (F4). In either case only the rows this receiver abandoned are handed back, by id.
    /// </summary>
    private async Task releaseAfterDrainAsync()
    {
        // Checked and registered under one lock, and held until the release statements are over: a receiver that
        // activates at this address meanwhile waits for it before it claims anything. See DurableReceiverRegistry.
        using var blanket = _custody.BatchMemberCount == 0
            ? DurableReceiverRegistry.TryBeginBlanketRelease(_runtime, this, releaseAddresses())
            : null;

        if (blanket != null)
        {
            // The blanket release covers everything abandoned so far at this receiver's own addresses. Members of a
            // cancelled batch are owned where they arrived, which is usually somewhere else, so they keep their turn
            // in the release by id below.
            var elsewhere = new List<Envelope>();
            while (_abandoned.TryDequeue(out var abandoned))
            {
                if (abandoned.Destination == null || !AnswersTo(abandoned.Destination)) elsewhere.Add(abandoned);
            }

            foreach (var envelope in elsewhere) _abandoned.Enqueue(envelope);

            await releaseIncomingAsync().ConfigureAwait(false);
            blanket.Dispose();
        }
        else
        {
            _logger.LogDebug(
                "Releasing only the inbox rows abandoned by the drain at {Uri}: other work at this address is still in progress on this node",
                Uri);
            await releaseAbandonedAsync().ConfigureAwait(false);
        }

        _released = true;

        // Anything abandoned while the release above was running
        await releaseAbandonedAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Track an envelope this receiver owns but will never execute, so its row is handed back. A batch carrier has
    /// no row: what it abandons are its members, owned at the addresses they arrived on.
    /// </summary>
    private void abandon(Envelope envelope)
    {
        if (envelope.Batch != null)
        {
            // The pending counts settle with the batch's terminal, and this is one
            if (_runtime is WolverineRuntime runtime)
            {
                runtime.BatchingPendingCounts.SettleBatch(envelope);
            }

            foreach (var member in envelope.Batch)
            {
                // No longer in a batch: nothing here will run it again, and its row is handed back below
                member.InBatch = false;
                InboxCustody.TrySettleBatchMember(member);
                _abandoned.Enqueue(member);
            }
        }
        else
        {
            _abandoned.Enqueue(envelope);
        }

        if (_released)
        {
            _ = releaseAbandonedInBackgroundAsync();
        }
    }

    private async Task releaseAbandonedInBackgroundAsync()
    {
        try
        {
            await releaseAbandonedAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error releasing abandoned inbox rows at {Uri}", Uri);
        }
    }

    /// <summary>
    /// Hand the rows of abandoned envelopes back to any node, by id and address
    /// </summary>
    private async Task releaseAbandonedAsync()
    {
        List<Envelope>? abandoned = null;
        while (_abandoned.TryDequeue(out var envelope))
        {
            if (envelope.Destination == null || envelope.Id == Guid.Empty) continue;
            (abandoned ??= new List<Envelope>()).Add(envelope);
        }

        if (abandoned == null) return;

        // ReassignIncomingAsync on a multi-tenanted store takes one tenant at a time
        foreach (var group in abandoned.DistinctBy(x => x.Id).GroupBy(x => (Store: x.Store ?? _runtime.Storage, x.TenantId)))
        {
            var envelopes = group.ToArray();
            await executeWithRetriesAsync(() =>
                group.Key.Store.ReassignIncomingAsync(TransportConstants.AnyNode, envelopes)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A second <c>received_at</c> address this receiver executes rows from, released alongside its own on
    /// drain. Set on a global partition's companion local queue to the slot's external address: a durable
    /// database-backed slot writes the inbox row at the SLOT's address when it pops (GH-4288), and the bridge
    /// then hands the envelope to this receiver, so the companion queue's backlog is split across two
    /// addresses. Releasing only <see cref="Uri"/> on drain left every row that had come through the shard
    /// queue table Incoming and owned by the ex-owner -- a live node, so neither the orphan sweep nor the next
    /// owner's recovery loop would ever look at it. Null everywhere else.
    /// </summary>
    internal Uri? AlsoReleaseIncomingAt { get; set; }

    private async Task releaseIncomingAsync()
    {
        await executeWithRetriesAsync(() => _inbox.ReleaseIncomingAsync(_settings.AssignedNodeNumber, Uri)).ConfigureAwait(false);

        // Deliberately on the same path as the primary release, so the GH-4797 hold on in-flight work applies
        // to both addresses: an envelope that came through the shard queue is just as much "still executing
        // here" as one that took the local shortcut.
        if (AlsoReleaseIncomingAt != null)
        {
            var alsoReleaseAt = AlsoReleaseIncomingAt;
            await executeWithRetriesAsync(() => _inbox.ReleaseIncomingAsync(_settings.AssignedNodeNumber, alsoReleaseAt)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// GH-4797. Hold the release until the work this drain could not wait out has actually finished, instead of
    /// handing rows to <c>AnyNode</c> underneath a running handler.
    ///
    /// <para>Deliberately not awaited. The drain is on the path an exclusive-listener agent's <c>StopAsync</c>
    /// returns through, and <c>ReassignAgent</c> gates the gaining node's start on that return -- so blocking
    /// here until a slow handler finished would convert a correctness problem into a slot that is served
    /// nowhere while it waits, which is the worse failure (GH-3856's direction). The stop still completes
    /// promptly; what changes is that nothing else can claim these rows in the meantime.</para>
    ///
    /// <para>The rows are not stranded in the normal case. A handler that finishes settles its own row, and
    /// anything merely queued-and-abandoned is released by the continuation below. The residual case is a
    /// handler that never returns at all: those rows stay owned by this node until it stops -- where
    /// <c>ReleaseAllOwnershipAsync</c> covers them -- or until the orphan sweep reclaims them after it is
    /// declared dead. That is the deliberate trade for this fix: a bounded delay in placing a message, rather
    /// than two nodes running it at once.</para>
    ///
    /// <para>One caveat worth naming, since it bounds what this can promise: the retry blocks above have
    /// already been drained by the time a late handler completes, so its mark-as-handled may not land. The
    /// release below then legitimately offers that row to another node, and it runs a second time. That is
    /// at-least-once behaving as documented -- and strictly better than the same duplicate happening
    /// <em>concurrently</em>, which is what this method exists to stop.</para>
    /// </summary>
    private void deferReleaseUntilIdleAsync(Task stillRunning)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // The task was started by DrainAsync, not here -- which is the whole point: this is the
                // handler work that drain could not wait out. Same suppression as AgentWorkConfirmation.
#pragma warning disable VSTHRD003
                await stillRunning.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            }
            catch (Exception e)
            {
                // The wait faulting tells us nothing about whether handlers are still running, so fall
                // through to the release rather than holding the rows on an unknown.
                _logger.LogDebug(e,
                    "Error waiting for in-flight message processing to finish at {Uri} before releasing its inbox rows",
                    Uri);
            }

            try
            {
                await releaseAfterDrainAsync().ConfigureAwait(false);

                _logger.LogInformation(
                    "Released the held inbox rows for {Uri} now that the work outstanding at drain time has finished. See GH-4797",
                    Uri);
            }
            catch (Exception e)
            {
                // Nothing above this to catch it -- and a throw out of a fire-and-forget task is an
                // unobserved exception, not a diagnostic. The rows stay owned by this node and are reclaimed
                // when it stops or is declared dead.
                _logger.LogError(e,
                    "Error releasing the held inbox rows for {Uri} after its drain timed out; they will be reclaimed when this node stops",
                    Uri);
            }
        });
    }

    public void Dispose()
    {
        // Might need to drain the block
        _batchRetries.Dispose();
        _receiver.Complete();

        // F18. Stops the heartbeat now if nothing is held, or once whatever is still held settles
        _disposed = true;
        stopHeartbeatIfIdle();

        _completeBlock.Dispose();
        _deferBlock.Dispose();
    }

    /// <summary>
    ///     GH-5105. The pauses between attempts at one dead-letter write before the move is given up on. The write is
    ///     awaited by whoever asked for the move, so this budget is kept short.
    /// </summary>
    internal TimeSpan[] DeadLetterWritePauses { get; set; } =
        [50.Milliseconds(), 250.Milliseconds(), 1.Seconds(), 2.Seconds()];

    /// <summary>
    ///     GH-5105. How long an envelope that could not be dead lettered waits before it is executed again
    /// </summary>
    internal TimeSpan DeadLetterRedeliveryDelay { get; set; } = 30.Seconds();

    public async Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        envelope.Failure = exception;
        DeadLetterQueueConstants.StampFailureMetadata(envelope, exception, _runtime.DurabilitySettings.DeadLetterFingerprinter);

        // GH-5105. This used to post the write to a background RetryBlock and return, so the caller completed the
        // envelope -- marking the inbox row, the only copy, handled -- and reported it as dead lettered before
        // anything was written. A write that kept failing was then discarded with one line at Information.
        var failure = await tryWriteDeadLetterAsync(envelope).ConfigureAwait(false);
        if (failure == null)
        {
            // A batch member dead lettered on its own is finished with by its batch
            InboxCustody.TrySettleBatchMember(envelope);
            return;
        }

        // Nothing was dead lettered, so hand the row back to be executed again rather than leaving it Incoming
        // and owned by this live node, which nothing reclaims until the node restarts (GH-4664)
        envelope.OwnerId = TransportConstants.AnyNode;
        envelope.ScheduledTime = DateTimeOffset.UtcNow.Add(DeadLetterRedeliveryDelay);
        envelope.Status = EnvelopeStatus.Scheduled;
        await _rescheduleAfterFailedDeadLetter.PostAsync(envelope).ConfigureAwait(false);
        InboxCustody.TrySettleBatchMember(envelope);

        _logger.LogError(failure,
            "Could not move envelope {EnvelopeId} ({MessageType}, tenant {TenantId}) received at {Uri} to dead letter storage. It was NOT dead lettered, and will be executed again after {Delay}",
            envelope.Id, envelope.MessageType, envelope.TenantId, Uri, DeadLetterRedeliveryDelay);

        throw new DeadLetterMoveFailedException(
            $"Envelope {envelope.Id} could not be moved to dead letter storage; it was scheduled to be executed again in {DeadLetterRedeliveryDelay}",
            failure);
    }

    /// <summary>
    ///     An envelope dead lettered as it arrives never reached the inbox, so the broker delivery is its only copy:
    ///     settle it only once the dead-letter write has been made, and otherwise hand it back to the broker
    /// </summary>
    private async Task deadLetterOnReceiptAsync(Envelope envelope)
    {
        var failure = await tryWriteDeadLetterAsync(envelope).ConfigureAwait(false);
        if (failure == null)
        {
            await _completeBlock.PostAsync(envelope).ConfigureAwait(false);
            return;
        }

        _logger.LogError(failure,
            "Could not move envelope {EnvelopeId} ({MessageType}, tenant {TenantId}) received at {Uri} to dead letter storage. It was NOT dead lettered, and is handed back to the transport to be delivered again",
            envelope.Id, envelope.MessageType, envelope.TenantId, Uri);

        if (envelope.Listener != null)
        {
            await _deferBlock.PostAsync(envelope).ConfigureAwait(false);
        }
    }

    /// <returns>Null if the dead-letter write was made, or the last failure if it could not be</returns>
    private async Task<Exception?> tryWriteDeadLetterAsync(Envelope envelope)
    {
        var pauses = DeadLetterWritePauses;
        for (var attempt = 0;; attempt++)
        {
            try
            {
                if (_deadLetterSender != null)
                {
                    await _deadLetterSender.SendAsync(envelope).ConfigureAwait(false);
                }
                else
                {
                    await _inbox.MoveToDeadLetterStorageAsync(envelope, envelope.Failure).ConfigureAwait(false);
                }

                return null;
            }
            catch (Exception e)
            {
                if (attempt >= pauses.Length || _settings.Cancellation.IsCancellationRequested) return e;

                _logger.LogWarning(e,
                    "Error moving envelope {EnvelopeId} received at {Uri} to dead letter storage on attempt {Attempt}; trying again",
                    envelope.Id, Uri, attempt + 1);

                try
                {
                    await Task.Delay(pauses[attempt], _settings.Cancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return e;
                }
            }
        }
    }

    private Task scheduleExecutionAsync(Envelope envelope)
    {
        return _endpoint is IDatabaseBackedEndpoint db
            ? db.ScheduleRetryAsync(envelope, _settings.Cancellation)
            : _inbox.ScheduleExecutionAsync(envelope);
    }

    public bool NativeDeadLetterQueueEnabled => true;

    public Task MoveToScheduledUntilAsync(Envelope envelope, DateTimeOffset time)
    {
        if (envelope.Batch != null)
        {
            // GH-5120. Rescheduling by the carrier's id updated nothing -- the carrier has no row -- so the retry
            // was silently lost and the members sat 'Incoming', owned by this live node. The members' rows stay
            // exactly as they are: owned by this node, at the address each member arrived on, while the retry is
            // pending. If this receiver drains first, it hands them back by id (F4) -- a blanket release of this
            // address would miss every member that arrived on a broker listener -- and recovery finds them if this
            // node goes away. The same carrier comes back, so its Attempts keeps counting toward the error
            // policy's limit.
            _logger.LogDebug("Scheduling batch envelope {EnvelopeId} ({MessageType}) for retry in memory at {ScheduledTime} in durable receiver", envelope.Id, envelope.MessageType, time);

            if (_draining)
            {
                // Nothing scheduled here would ever fire: hand the members back now
                abandon(envelope);
                return Task.CompletedTask;
            }

            _pendingBatchRetries[envelope.Id] = envelope;
            _batchRetries.Enqueue(time, envelope);
            return Task.CompletedTask;
        }

        return scheduleAsync(envelope, time);
    }

    private async Task scheduleAsync(Envelope envelope, DateTimeOffset time)
    {
        _logger.LogDebug("Moving envelope {EnvelopeId} ({MessageType}) to scheduled status until {ScheduledTime} in durable receiver", envelope.Id, envelope.MessageType, time);
        envelope.OwnerId = TransportConstants.AnyNode;
        envelope.ScheduledTime = time;
        envelope.Status = EnvelopeStatus.Scheduled;

        await _scheduleExecution.PostAsync(envelope).ConfigureAwait(false);

        // A batch member rescheduled on its own is finished with by its batch
        InboxCustody.TrySettleBatchMember(envelope);
    }

    /// <summary>
    /// GH-4435. Test seam: has this receiver asked its listening agent to pause for inbox recovery?
    /// </summary>
    internal bool InboxUnavailableSignaled => _inboxUnavailableSignaled == 1;

    /// <summary>
    /// GH-4435. Pause the listener only when the failure reached the MAIN message store. A single tenant
    /// database being unreachable must not stop a listener that serves every other tenant: those
    /// envelopes are deferred back to the broker on the per-envelope path while everyone else keeps
    /// flowing.
    /// </summary>
    private void signalInboxUnavailableUnlessTenantScoped(Exception e)
    {
        if (e is TenantedInboxWriteException { IncludesMainStore: false } tenanted)
        {
            // GH-5218. Name the tenant(s): "one or more tenant databases" alone left an operator unable to
            // tell which tenant was stranded without decoding the exception.
            _logger.LogWarning(e,
                "Inbox write failed for {Count} envelope(s) against the message store(s) for tenant(s) {TenantIds} at {Uri}. The listener keeps running; those envelopes are deferred back to the broker",
                tenanted.Unpersisted.Count, string.Join(", ", tenanted.TenantIds), Uri);
            return;
        }

        SignalInboxUnavailable(e);
    }

    internal void SignalInboxUnavailable(Exception? cause = null)
    {
        if (Interlocked.CompareExchange(ref _inboxUnavailableSignaled, 1, 0) != 0) return;

        // GH-5218. Carry the exception that caused the pause. Without it, the log said the inbox was
        // unavailable and nothing else -- not which store, not which tenant, not why.
        _logger.LogWarning(cause, "Inbox database unavailable for {Uri}. Signaling listener to pause.", Uri);

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
            // GH-4288. An envelope already persisted in the inbox (a durable database-backed queue's
            // dequeue, or store-driven recovery) has a row for the recovery sweeps to find; storing it
            // again could only throw DuplicateIncomingEnvelopeException to be swallowed below.
            if (!envelope.IsFromLocalDurableQueue() && !envelope.WasPersistedInInbox)
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

        // GH-4288. WasPersistedInInbox: a durable database-backed queue moves the envelope into the
        // inbox as part of the dequeue, and the GlobalPartitionedReceiverBridge then forwards it to a
        // companion local queue whose receiver is NOT database-backed -- so the endpoint-level
        // ShouldPersistBeforeProcessing check alone re-stored an already-persisted row here.
        if (ShouldPersistBeforeProcessing && !envelope.IsFromLocalDurableQueue() && !envelope.WasPersistedInInbox)
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
                    await deadLetterOnReceiptAsync(envelope).ConfigureAwait(false);
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
                    await deadLetterOnReceiptAsync(envelope).ConfigureAwait(false);
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
            catch (Exception outer)
            {
                // GH-4435. A tenant-scoped failure defers this envelope back to the broker below without
                // pausing the listener for every other tenant.
                signalInboxUnavailableUnlessTenantScoped(outer);

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

        if (envelope.Listener == null) return;

        // GH-4012 item 4. THIS is where the redeliver -> dedupe -> re-ack loop turns: the settle below
        // cannot succeed, so the broker redelivers, the inbox deduplicates it again, and around it goes.
        // Envelope.AckAttempts cannot bound that -- every redelivery arrives as a brand new envelope with
        // a fresh counter -- but the broker's own delivery count survives, which is the whole reason it
        // exists. Past the limit, hand the delivery to the dead letter queue instead of settling it again.
        if (HasExhaustedBrokerRedeliveries(envelope) &&
            envelope.Listener is ISupportDeadLetterQueue { NativeDeadLetterQueueEnabled: true } deadLetters)
        {
            try
            {
                await deadLetters.MoveToErrorsAsync(envelope, e).ConfigureAwait(false);

                // GH-4488: MoveToErrorsAsync is not uniformly terminal. On most transports it settles the
                // delivery -- RabbitMQ nacks to the DLX, Azure Service Bus dead letters it -- and there is
                // nothing left to do. But SQS and GCP Pub/Sub implement it as a COPY to another queue/topic
                // and leave the original delivery completely untouched, so returning here would strand it:
                // the visibility timeout lapses, the broker delivers it again, the inbox deduplicates it
                // again, and because BrokerDeliveryCount only ever grows it is dead lettered again. The loop
                // this whole branch exists to break would instead emit one dead letter copy per turn.
                //
                // HasBeenAcked is how a transport reports that it settled -- the same flag
                // MessageContext.CompleteAsync short-circuits on, which RabbitMQ sets next to its nack and
                // Azure Service Bus next to its dead letter move (GH-4481). Still clear means nothing
                // settled this delivery, so settle it here.
                if (!envelope.HasBeenAcked)
                {
                    await envelope.Listener.CompleteAsync(envelope).ConfigureAwait(false);
                }

                _logger.LogWarning(
                    "Moved envelope {Id} from {Uri} to the dead letter queue after the broker delivered it {DeliveryCount} times; it is a duplicate that cannot be settled",
                    envelope.Id, Uri, envelope.BrokerDeliveryCount);

                return;
            }
            catch (Exception exception)
            {
                // Fall through to the ordinary settle -- a dead letter move that fails is no worse than
                // the loop we were already in
                _logger.LogError(exception, "Error trying to dead letter over-delivered message {Id} from {Uri}",
                    envelope.Id, Uri);
            }
        }

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

    /// <summary>
    /// GH-4012 item 4. Has the broker delivered this message more times than
    /// <see cref="DurabilitySettings.MaximumBrokerRedeliveries" /> allows? False when the limit is off
    /// (the default) or the transport reports no delivery count.
    /// </summary>
    internal bool HasExhaustedBrokerRedeliveries(Envelope envelope)
    {
        var limit = _settings.MaximumBrokerRedeliveries;
        return limit > 0 && envelope.BrokerDeliveryCount is { } count && count > limit;
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
            // GH-4288. See receiveOneAsync -- WasPersistedInInbox marks an envelope a durable
            // database-backed dequeue (or store-driven recovery) already put in the inbox.
            if (ShouldPersistBeforeProcessing && !envelope.IsFromLocalDurableQueue() && !envelope.WasPersistedInInbox)
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
                    await deadLetterOnReceiptAsync(envelope).ConfigureAwait(false);
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
                    await deadLetterOnReceiptAsync(envelope).ConfigureAwait(false);
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

        // GH-4288. Leave out envelopes a durable database-backed dequeue already moved into the
        // inbox -- batch-storing them again would throw DuplicateIncomingEnvelopeException for
        // the whole batch.
        var toPersist = ShouldPersistBeforeProcessing
            ? envelopes.Where(x => !x.WasPersistedInInbox).ToArray()
            : Array.Empty<Envelope>();

        if (toPersist.Length > 0)
        {
            try
            {
                assignAncillaryStoreIfNeeded(toPersist);
                await _inbox.StoreIncomingAsync(toPersist).ConfigureAwait(false);
                foreach (var envelope in toPersist)
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
                // GH-4658. Only the failures that actually mean "nothing can be persisted" are an Error
                // here. A tenant-scoped failure gets its own Warning from the helper below, naming the
                // same Uri and the envelope count -- and while a tenant's database is down its envelopes
                // are redelivered continuously, so logging both lines per turn buried the log in Errors
                // over a condition the listener is deliberately riding out.
                if (e is not TenantedInboxWriteException { IncludesMainStore: false })
                {
                    _logger.LogError(e, "Error trying to persist incoming envelopes at {Uri}", Uri);
                }

                // GH-4435. Envelopes whose own store committed are already stamped WasPersistedInInbox by
                // MultiTenantedMessageStore, so the per-envelope path below acks and enqueues those and
                // only re-attempts the ones that never landed.
                signalInboxUnavailableUnlessTenantScoped(e);

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

    public async Task ClearInFlightIncomingAsync()
    {
        // F16. A listener builds a new receiver on every restart -- after a pause, a forced restart, a circuit
        // breaker, or InboxHealthRestarter -- and this blanket release ran every time. The previous receiver at
        // this address is often still finishing a handler then (GH-4797 holds its rows for exactly that reason),
        // or still holding batch members, and this handed all of those rows to AnyNode underneath it. That
        // receiver hands back what it abandoned itself once its work is done, so skip the sweep here.
        using var blanket = DurableReceiverRegistry.TryBeginBlanketRelease(_runtime, this, [Uri]);
        if (blanket == null)
        {
            _logger.LogDebug(
                "Not releasing the in-flight inbox rows at {Uri}: a previous receiver at this address is still working on some of them, and releases its own when it finishes",
                Uri);
            return;
        }

        await executeWithRetriesAsync(() => _inbox.ReleaseIncomingAsync(_settings.AssignedNodeNumber, Uri))
            .ConfigureAwait(false);
    }

    public void Latch()
    {
        _latched = true;
    }
}
