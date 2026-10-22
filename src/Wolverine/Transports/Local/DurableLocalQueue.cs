using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports.Sending;

namespace Wolverine.Transports.Local;

internal class DurableLocalQueue : ISendingAgent, IListenerCircuit, ILocalQueue
{
    private readonly IMessageInbox _inbox;
    private readonly ILogger _logger;
    private readonly IMessageTracker _messageLogger;
    private readonly WolverineRuntime _runtime;
    private readonly IMessageSerializer _serializer;
    private readonly DurabilitySettings _settings;

    // GH-4319. The local durable queue was the last per-message inbox INSERT that never batched: every
    // publish opened its own pooled connection for one row. Null when StoreIncomingBatchSize is 1.
    private readonly EnvelopeStoreCoalescer? _storeCoalescer;
    private DurableReceiver? _receiver;
    private Restarter? _restarter;

    // F6. What reached this queue while it was paused -- a batch carrier assembled while its batch queue was paused,
    // above all -- held here until the queue resumes. Guarded by _receiverLock, together with the swap of _receiver.
    private readonly object _receiverLock = new();
    private List<Envelope>? _heldWhilePaused;

    // L1. The drain of the receiver a pause took away, when that pause did not wait for it. The restart waits for it
    // so the old receiver's release can never hand back rows the new receiver has already taken.
    private Task? _pausedDrain;

    public DurableLocalQueue(Endpoint endpoint, WolverineRuntime runtime)
    {
        Uri = endpoint.Uri;
        _settings = runtime.DurabilitySettings;

        // When ancillary stores exist, wrap the inbox so that envelopes whose
        // Store property has already been stamped (by ApplyAncillaryStoreFrame
        // during handler execution) are persisted in the correct database.
        // Without this, all local-queue messages land in the main store's inbox
        // regardless of the handler's ancillary store association.
        _inbox = runtime.Stores != null && runtime.Stores.HasAnyAncillaryStores()
            ? new DelegatingMessageInbox(runtime.Storage.Inbox, runtime.Stores)
            : runtime.Storage.Inbox;

        _messageLogger = runtime.MessageTrackingFor(endpoint);
        _serializer = endpoint.DefaultSerializer ??
                      throw new ArgumentOutOfRangeException(nameof(endpoint),
                          "No default serializer for this Endpoint");
        Destination = endpoint.Uri;

        _runtime = runtime;

        Endpoint = endpoint;
        ReplyUri = TransportConstants.RepliesUri;

        _logger = runtime.LoggerFactory.CreateLogger<DurableLocalQueue>();

        if (endpoint.CircuitBreakerOptions != null)
        {
            CircuitBreaker = new CircuitBreaker(endpoint.CircuitBreakerOptions, this, runtime.Observer);
            Pipeline = new HandlerPipeline(runtime, new CircuitBreakerTrackedExecutorFactory(CircuitBreaker, runtime),
                endpoint)
            {
                TelemetryEnabled = endpoint.TelemetryEnabled
            };
        }
        else
        {
            Pipeline = new HandlerPipeline(runtime, runtime, endpoint);
        }

        _receiver = new DurableReceiver(endpoint, runtime, Pipeline);

        if (_settings.StoreIncomingBatchSize > 1)
        {
            _storeCoalescer = new EnvelopeStoreCoalescer(
                envelopes => _inbox.StoreIncomingAsync(envelopes),
                envelope => _inbox.StoreIncomingAsync(envelope),
                _settings.StoreIncomingBatchSize, Uri, _logger);
        }
    }

    public CircuitBreaker? CircuitBreaker { get; }

    /// <summary>
    /// Immediately latch the receiver to stop processing new messages.
    /// </summary>
    public void LatchReceiver()
    {
        Latched = true;
        _receiver?.Latch();
    }

    int IListenerCircuit.QueueCount => _receiver?.QueueCount ?? 0;

    async Task IListenerCircuit.EnqueueDirectlyAsync(IEnumerable<Envelope> envelopes)
    {
        // F17. Every caller of this has already claimed these rows for this node -- the scheduled poller, inbox
        // recovery -- and the only thing that can give them back is that caller, on an exception (GH-3680, GH-4822).
        // Returning quietly from a paused queue left them Incoming and owned by a live node, which nothing reclaims.
        // Same answer ListeningAgent gives when it has no receiver.
        var receiver = _receiver ?? throw new InvalidOperationException(
            $"The durable local queue at {Uri} is paused and cannot accept recovered or scheduled messages until it resumes");

        foreach (var envelope in envelopes) await receiver.EnqueueAsync(envelope);
    }

    public ValueTask PauseAsync(TimeSpan pauseTime)
    {
        // L1. Usually raised from inside this queue's own handler pipeline (AndPauseProcessing, PauseThenRequeue),
        // so it must not wait for the queue to drain: the frame that asked for the pause is part of what the drain
        // waits for, and it sat out the whole DrainTimeout waiting on itself. The drain still latches first and
        // still waits for running handlers before it releases their rows (GH-4797) -- it just does so in the
        // background, where the frame that asked for it can finish.
        return pauseCoreAsync(pauseTime, waitForDrain: false);
    }

    public ValueTask PauseWithDrainAsync(TimeSpan pauseTime)
    {
        // Only ever called from outside the pipeline (the circuit breaker), so waiting here is safe.
        return pauseCoreAsync(pauseTime, waitForDrain: true);
    }

    private async ValueTask pauseCoreAsync(TimeSpan pauseTime, bool waitForDrain)
    {
        Latched = true;
        // GH-3832 — remember that this latch is a timed pause so Status reports Paused rather than
        // the TooBusy that a back-pressure latch reports. This one is released by the Restarter
        // installed at the end of this method; TooBusy is released by the queue draining.
        _paused = true;

        DurableReceiver? receiver;
        lock (_receiverLock)
        {
            receiver = _receiver;
            _receiver = null;
        }

        if (receiver != null)
        {
            // Latched BEFORE the drain, which is what makes DurableReceiver.DrainAsync wait for the handlers still
            // running before it releases their rows
            receiver.Latch();

            var drain = drainPausedReceiverAsync(receiver);
            if (waitForDrain)
            {
                await drain;
            }
            else
            {
                _pausedDrain = drain;
            }
        }

        CircuitBreaker?.Reset();

        _runtime.Tracker.Publish(
            new ListenerState(Endpoint.Uri, Endpoint.EndpointName, ListeningStatus.Paused));

        _logger.LogInformation("Pausing message listening at {Uri}", Endpoint.Uri);

        // A second pause while paused -- a sibling handler failing the same way -- restarts the clock instead of
        // leaving two restarters behind
        _restarter?.Dispose();
        _restarter = new Restarter(this, pauseTime);
    }

    private Task drainPausedReceiverAsync(DurableReceiver receiver)
    {
        // Task.Run, so nothing of the drain runs on the caller's frame, which may be a handler of this very queue
        return Task.Run(async () =>
        {
            try
            {
                await receiver.DrainAsync();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to drain in flight messages for {Uri}", Destination);
            }
        });
    }

    private Uri? _partitionSlotUri;

    /// <summary>
    /// When this queue is a global partition's companion local queue, the slot's external address. The slot's
    /// <c>ListeningAgent</c> stamps it when it installs the bridge, and every receiver this queue builds --
    /// the one from the constructor and every rebuild in <see cref="StartAsync"/> after a slot handoff -- carries
    /// it as <see cref="DurableReceiver.AlsoReleaseIncomingAt"/>, so losing the slot releases the rows that
    /// arrived through the shard queue table and not only the ones that took the local shortcut.
    /// </summary>
    internal Uri? PartitionSlotUri
    {
        get => _partitionSlotUri;
        set
        {
            _partitionSlotUri = value;
            if (_receiver != null)
            {
                _receiver.AlsoReleaseIncomingAt = value;
            }
        }
    }

    public async ValueTask StartAsync()
    {
        // L1. A pause that did not wait for its drain leaves it here. The drain ends in a release of every row this
        // node owns at this address, so it has to be over before a new receiver starts taking rows at the same one.
        // It cannot throw, and once the handlers running at pause time finish it is quick.
        var pausedDrain = Interlocked.Exchange(ref _pausedDrain, null);
        if (pausedDrain != null)
        {
#pragma warning disable VSTHRD003 // started by the pause, and awaiting it is the whole point
            await pausedDrain;
#pragma warning restore VSTHRD003
        }

        var receiver = new DurableReceiver(Endpoint, _runtime, Pipeline)
        {
            AlsoReleaseIncomingAt = _partitionSlotUri
        };

        // Counts as working at this address before the queue can claim a row for it, and waits out a blanket release
        // by the paused receiver that is still in flight, so that release cannot catch a row claimed for this one
        await receiver.ActivateAsync();

        List<Envelope>? held;
        lock (_receiverLock)
        {
            _receiver = receiver;
            held = _heldWhilePaused;
            _heldWhilePaused = null;
        }

        Latched = false;
        _paused = false;
        _runtime.Tracker.Publish(new ListenerState(receiver.Uri, Endpoint.EndpointName,
            ListeningStatus.Accepting));
        _restarter?.Dispose();
        _restarter = null;

        if (held != null)
        {
            foreach (var envelope in held) await receiver.EnqueueAsync(envelope);
        }
    }

    /// <summary>
    /// F6. Hold an envelope that reached this queue while it was paused, or answer the receiver to hand it to.
    /// </summary>
    private DurableReceiver? receiverOrHold(Envelope envelope)
    {
        var receiver = _receiver;
        if (receiver != null) return receiver;

        lock (_receiverLock)
        {
            if (_receiver != null) return _receiver;

            (_heldWhilePaused ??= []).Add(envelope);
            return null;
        }
    }

    private bool _paused;

    // GH-3832 — a timed pause must not read as the back-pressure TooBusy latch;
    // LatchReceiver() (agent-restriction latch) deliberately keeps the TooBusy mapping for now.
    ListeningStatus IListenerCircuit.Status => _paused
        ? ListeningStatus.Paused
        : Latched ? ListeningStatus.TooBusy : ListeningStatus.Accepting;

    public Uri Uri { get; }

    public IHandlerPipeline Pipeline { get; }

    public void Dispose()
    {
        _receiver?.SafeDispose();
        CircuitBreaker?.SafeDisposeSynchronously();
        _receiver?.SafeDispose();
    }

    ValueTask IReceiver.ReceivedAsync(IListener listener, Envelope[] messages)
    {
        return _receiver!.ReceivedAsync(listener, messages);
    }

    ValueTask IReceiver.ReceivedAsync(IListener listener, Envelope envelope)
    {
        return _receiver!.ReceivedAsync(listener, envelope);
    }

    async ValueTask IReceiver.DrainAsync()
    {
        var receiver = _receiver;

        receiver?.Latch();

        // GH-4662 removed the store-and-enqueue RetryBlock -- the write is awaited by its caller now --
        // so the coalescer is the only thing left here that can hold a started flush.
        if (_storeCoalescer != null)
        {
            await _storeCoalescer.DrainAsync();
        }

        if (receiver != null)
            await receiver.DrainAsync();
    }

    // F6. While paused there is no receiver. What is enqueued meanwhile -- a batch carrier above all, whose members'
    // rows depend on it reaching a terminal -- is held and runs when the queue resumes, rather than thrown away
    // (Enqueue) or thrown at a caller with no way to give it back (EnqueueAsync, the batching processor).
    void ILocalReceiver.Enqueue(Envelope envelope)
    {
        receiverOrHold(envelope)?.Enqueue(envelope);
    }

    ValueTask ILocalReceiver.EnqueueAsync(Envelope envelope)
    {
        var receiver = receiverOrHold(envelope);
        return receiver == null ? ValueTask.CompletedTask : receiver.EnqueueAsync(envelope);
    }

    int IHasQueueDepth.QueueCount => _receiver?.QueueCount ?? 0;

    public Uri Destination { get; }

    public Endpoint Endpoint { get; }

    public Uri? ReplyUri { get; set; }

    public bool Latched { get; private set; }

    public bool IsDurable => true;

    public async ValueTask EnqueueOutgoingAsync(Envelope envelope)
    {
        // The envelope would be persisted regardless
        if (Latched)
        {
            return;
        }

        _messageLogger.Sent(envelope);

        // A pause can land between the check above and here
        var receiver = receiverOrHold(envelope);
        if (receiver != null) await receiver.EnqueueAsync(envelope);
    }

    public ValueTask StoreAndForwardAsync(Envelope envelope)
    {
        // Try this first, let everything fail if it fails, don't want to log
        writeMessageData(envelope);

        _messageLogger.Sent(envelope);

        envelope.PrepareForIncomingPersistence(DateTimeOffset.UtcNow, _settings);

        if (Latched)
        {
            envelope.OwnerId = TransportConstants.AnyNode;
        }

        // GH-4662: awaited rather than posted to a RetryBlock, so a store that stays down fails the
        // publish instead of discarding the envelope behind the caller's back.
        return new ValueTask(storeAndEnqueueAsync(envelope));
    }

    public bool SupportsNativeScheduledSend => true;

    public DateTimeOffset LastMessageSentAt => DateTimeOffset.UtcNow;

    /// <summary>
    /// If the handler for this message type targets an ancillary store on a
    /// different database, set envelope.Store so that the DelegatingMessageInbox
    /// persists it in the correct store for transactional atomicity. The
    /// receiving handler's store association wins over the publishing context's
    /// store: a message published from a main-store handler can be persisted
    /// transactionally by an ancillary-store handler. Without overriding here,
    /// a publisher-stamped envelope.Store (the main store) would carry through
    /// the inbox and cause FlushOutgoingMessagesOnCommit to point at the
    /// publisher's inbox table while the receiving Marten/Polecat session was
    /// connected to the ancillary database. See GH-2669.
    /// </summary>
    private void assignAncillaryStoreIfNeeded(Envelope envelope)
    {
        if (_runtime.Stores == null) return;

        // Uri, not just the message type: a sticky handler makes the owning store endpoint specific,
        // and a local queue only ever feeds the handler chain for its own endpoint (GH-3886).
        var store = _runtime.Stores.TryFindAncillaryStoreForMessageType(Uri, envelope.MessageType);
        if (store != null)
        {
            envelope.Store = store;
        }
    }

    private async Task storeAndEnqueueAsync(Envelope envelope)
    {
        var isLatched = Latched;

        try
        {
            // Use AnyNode when latched so the durability agent can recover the message.
            envelope.OwnerId = isLatched
                ? TransportConstants.AnyNode
                : _settings.AssignedNodeNumber;

            // The ancillary store has to be stamped BEFORE the envelope joins a batch: both
            // DelegatingMessageInbox and MultiTenantedMessageStore group the batched overload by
            // Store/TenantId, and they can only group on what is already on the envelope.
            assignAncillaryStoreIfNeeded(envelope);

            // GH-4662: retried inline, and on exhaustion the exception reaches whoever called
            // PublishAsync/ScheduleAsync. Before this it was swallowed by a RetryBlock that had
            // already handed the caller a completed task, so a store outage lost the message with
            // nothing but an Information line to show for it.
            await DurableWriteRetry.ExecuteAsync(
                () => _storeCoalescer != null
                    ? _storeCoalescer.StoreAsync(envelope)
                    : _inbox.StoreIncomingAsync(envelope),
                envelope, _logger, _runtime.Cancellation);

            envelope.WasPersistedInInbox = true;
        }
        catch (DuplicateIncomingEnvelopeException e)
        {
            _logger.LogError(e, "Duplicate incoming envelope detected");
            return;
        }

        if (isLatched)
        {
            return;
        }

        if (envelope.Status == EnvelopeStatus.Incoming)
        {
            // The row was written owned by this node, so a pause that landed during the write must not lose it:
            // held, it runs when the queue resumes
            var receiver = receiverOrHold(envelope);
            if (receiver != null) await receiver.EnqueueAsync(envelope);
        }
    }

    private void writeMessageData(Envelope envelope)
    {
        if (envelope.Message is null)
        {
            throw new ArgumentOutOfRangeException(nameof(envelope), "Envelope.Message is null");
        }

        if (envelope.Data == null || envelope.Data.Length == 0)
        {
            _serializer.Write(envelope);
            envelope.ContentType = _serializer.ContentType;
        }
    }
}