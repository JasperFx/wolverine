using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Partitioning;
using Wolverine.Runtime.WorkerQueues;

namespace Wolverine.Transports;

public interface IListenerCircuit
{
    ListeningStatus Status { get; }
    Endpoint Endpoint { get; }
    int QueueCount { get; }
    ValueTask PauseAsync(TimeSpan pauseTime);
    
    /// <summary>
    /// Pause the listener and fully drain any buffered messages before returning.
    /// Unlike PauseAsync (which may skip the drain to avoid deadlocks when called from
    /// within the handler pipeline), this method guarantees all queued messages are
    /// processed before returning. Safe to call from background threads.
    /// </summary>
    ValueTask PauseWithDrainAsync(TimeSpan pauseTime);
    
    ValueTask StartAsync();

    /// <summary>
    /// Force the listener to stop and rebuild its underlying transport listener even if it currently reports
    /// <see cref="ListeningStatus.Accepting"/>. This is the remediation primitive for recovering a "stuck"
    /// listener — e.g. a dead transport channel that the framework could not self-heal but that still reports
    /// Accepting — without bouncing the process. When <paramref name="force"/> is <c>false</c> this behaves like
    /// <see cref="StartAsync"/> (a no-op when already Accepting). The default implementation is the gentle
    /// <see cref="StartAsync"/>; circuits backed by a real transport listener override it to tear down and rebuild.
    /// </summary>
    ValueTask RestartAsync(bool force = true) => StartAsync();

    Task EnqueueDirectlyAsync(IEnumerable<Envelope> envelopes);
}

public interface IListeningAgent : IListenerCircuit
{
    Uri Uri { get; }

    /// <summary>
    /// Approximate timestamp of the last time queue activity was observed on this listener.
    /// Based on QueueCount change detection, not individual message receipt.
    /// </summary>
    DateTimeOffset LastQueueActivityAt { get; }

    ValueTask StopAndDrainAsync();

    ValueTask MarkAsTooBusyAndStopReceivingAsync();

    ValueTask LatchPermanently();

    /// <summary>
    /// CritterWatch#942 — true when this listener's receiver execution block has faulted terminally
    /// (jasperfx#506). A faulted receiver can never make progress: its QueueCount freezes and every
    /// post from the receive loop throws, so the listener looks alive while dropping everything it
    /// receives. BackPressureAgent forces a full rebuild when it sees this; health checks should
    /// treat it as unhealthy. Default false for implementations that don't track it.
    /// </summary>
    bool ReceiverHasFaulted => false;
}

public class ListeningAgent : IAsyncDisposable, IDisposable, IListeningAgent
{
    private readonly BackPressureAgent? _backPressureAgent;
    private readonly CircuitBreaker? _circuitBreaker;
    private readonly ILogger _logger;
    private readonly HandlerPipeline _pipeline;
    private readonly WolverineRuntime _runtime;
    private IReceiver? _receiver;
    private IDisposable? _restarter;
    private ListenerInboxRecoveryLoop? _inboxRecovery;
    private int _lastObservedQueueCount;
    private DateTimeOffset _lastQueueCountChangeAt = DateTimeOffset.UtcNow;
    private bool _disposed;

    public ListeningAgent(Endpoint endpoint, WolverineRuntime runtime)
    {
        Endpoint = endpoint;
        _runtime = runtime;
        Uri = endpoint.Uri;
        _logger = runtime.LoggerFactory.CreateLogger<ListeningAgent>();

        if (endpoint.CircuitBreakerOptions != null)
        {
            _circuitBreaker = new CircuitBreaker(endpoint.CircuitBreakerOptions, this, runtime.Observer);
            _pipeline = new HandlerPipeline(runtime,
                new CircuitBreakerTrackedExecutorFactory(_circuitBreaker,
                    new CircuitBreakerTrackedExecutorFactory(_circuitBreaker, runtime)), endpoint)
            {
                TelemetryEnabled = endpoint.TelemetryEnabled
            };
        }
        else
        {
            _pipeline = new HandlerPipeline(runtime, runtime, endpoint)
            {
                TelemetryEnabled = endpoint.TelemetryEnabled
            };
        }

        if (endpoint.ShouldEnforceBackPressure())
        {
            _backPressureAgent = new BackPressureAgent(this, endpoint, runtime.Observer, _logger);
            _backPressureAgent.Start();
        }
    }

    public IListener? Listener { get; private set; }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        _restarter?.SafeDispose();
        _backPressureAgent?.SafeDispose();
        stopInboxRecovery();

        if (Listener != null)
        {
            await Listener.DisposeAsync();
        }

        _receiver?.Dispose();

        if (_circuitBreaker != null)
        {
            await _circuitBreaker.DisposeAsync();
        }
        
        Listener = null;
        _receiver = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _receiver?.Dispose();
        _circuitBreaker?.SafeDisposeSynchronously();
        _backPressureAgent?.SafeDispose();
        stopInboxRecovery();
        _semaphore.Dispose();
    }

    // CritterWatch#942 — the receive block's own depth PLUS the members this listener has fed into
    // message-batching pipelines that haven't reached a batch terminal yet. Without the second term,
    // BatchMessagesOf drains the (bounded, watched) receive block into an unbounded local execution
    // queue and back-pressure never fires while memory grows without bound.
    /// <summary>
    /// CritterWatch#942 — see <see cref="IListeningAgent.ReceiverHasFaulted"/>. True when the
    /// current receiver's execution block has faulted terminally (jasperfx#506).
    /// </summary>
    public bool ReceiverHasFaulted => _receiver is IFaultTrackingReceiver { HasFaulted: true };

    public int QueueCount => (_receiver is ILocalQueue q ? q.QueueCount : 0)
                             + _runtime.BatchingPendingCounts.PendingFor(Endpoint.Uri);

    /// <summary>
    /// Approximate timestamp of the last time a message was received on this listener,
    /// tracked by observing QueueCount changes. Updated by BackPressureAgent polling
    /// or explicit calls to <see cref="UpdateQueueCountObservation"/>.
    /// </summary>
    public DateTimeOffset LastQueueActivityAt => _lastQueueCountChangeAt;

    /// <summary>
    /// Call periodically to update the heuristic for last message received.
    /// If QueueCount has changed since the last observation, the timestamp is updated.
    /// </summary>
    internal void UpdateQueueCountObservation()
    {
        var current = QueueCount;
        if (current != _lastObservedQueueCount)
        {
            _lastQueueCountChangeAt = DateTimeOffset.UtcNow;
            _lastObservedQueueCount = current;
        }
    }

    public async Task EnqueueDirectlyAsync(IEnumerable<Envelope> envelopes)
    {
        if (_receiver is BufferedReceiver)
        {
            // Agent is latched if listener is null
            await _receiver.ReceivedAsync(new RetryOnInlineChannelCallback(Listener!, _runtime), envelopes.ToArray());
        }
        else if (_receiver is ILocalQueue queue)
        {
            var uniqueNodeId = _runtime.DurabilitySettings.AssignedNodeNumber;
            foreach (var envelope in envelopes)
            {
                envelope.OwnerId = uniqueNodeId;
                await queue.EnqueueAsync(envelope);
            }
        }
        else if (_receiver is InlineReceiver inline)
        {
            // Agent is latched if listener is null
            await inline.ReceivedAsync(new RetryOnInlineChannelCallback(Listener!, _runtime), envelopes.ToArray());
        }
        else if (_receiver is NativeAckReceiver nativeAck)
        {
            // GH-4011. Same shape as the InlineReceiver case above, and for the same reason: a receiver whose
            // settlement rides the listener rather than a local queue. These envelopes come from the message
            // store (DLQ replay per GH-1942, scheduled-message firing), not from a broker delivery, so there is
            // no delivery tag to ack -- RetryOnInlineChannelCallback marks the inbox row handled and only then
            // forwards to the real listener. Without this branch a NativeAck endpoint in an application that
            // also has persistence configured -- durable outbox for sending, native acks on one flooding
            // listener, a perfectly legitimate combination -- threw on any DLQ replay targeting it.
            // Agent is latched if listener is null
            await nativeAck.ReceivedAsync(new RetryOnInlineChannelCallback(Listener!, _runtime), envelopes.ToArray());
        }
        else if (_receiver is GlobalPartitionedReceiverBridge bridge)
        {
            // Forward to the companion local queue for sequential processing
            foreach (var envelope in envelopes)
            {
                await bridge.ReceivedAsync(Listener!, envelope);
            }
        }
        else
        {
            throw new InvalidOperationException("There is no active, local queue for this listening endpoint at " +
                                                Endpoint.Uri);
        }
    }

    public Endpoint Endpoint { get; }

    public Uri Uri { get; }

    public ListeningStatus Status { get; private set; } = ListeningStatus.Stopped;

    /// <summary>
    /// Immediately latch the receiver to stop processing new messages from its internal queue.
    /// Does not stop the listener or drain — just prevents the receiver from executing any more messages.
    /// </summary>
    public void LatchReceiver()
    {
        // GH-3709. Deliberately a single ILatchedReceiver test rather than an if/else chain naming each
        // receiver type. As a chain this silently missed NativeAckReceiver when GH-3708 added it, and an
        // unlatched receiver's DrainAsync returns immediately instead of waiting for in-flight handlers --
        // so a stop-and-drain closed the transport channel underneath running work, the unsettled deliveries
        // were requeued, and on an exclusive listener handoff the new owner re-ran them concurrently with
        // the old owner. That is exactly the intra-group concurrency the partitioned modes forbid.
        var actual = _receiver is ReceiverWithRules rwr ? rwr.Inner : _receiver;
        if (actual is ILatchedReceiver latched)
        {
            latched.Latch();
        }
    }

    public async ValueTask StopAndDrainAsync()
    {
        await StopAndDrainCoreAsync(latchBeforeDrain: true);
    }

    /// <summary>
    /// Shared implementation for stop-and-drain. When <paramref name="latchBeforeDrain"/> is
    /// <c>true</c> (normal shutdown), the receiver is latched before <see cref="IReceiver.DrainAsync"/>
    /// so that the drain knows it is safe to wait for any in-flight messages to complete.
    /// When <c>false</c> (pause triggered from within the handler pipeline, e.g. rate limiting),
    /// the receiver is <em>not</em> pre-latched, so <see cref="IReceiver.DrainAsync"/> sees
    /// <c>_latched == false</c> and returns immediately — avoiding a deadlock caused by the
    /// current message's execute frame being on the call stack.
    /// </summary>
    private async ValueTask StopAndDrainCoreAsync(bool latchBeforeDrain)
    {
        // GH-3590. Always tear the loop down first -- StartAsync() rebuilds it when this listener becomes
        // the active one again.
        stopInboxRecovery();

        if (Status is ListeningStatus.Stopped or ListeningStatus.GloballyLatched or ListeningStatus.Paused)
        {
            return;
        }

        var listener = Listener;
        var receiver = _receiver;
        if (listener == null)
        {
            return;
        }

        try
        {
            using var activity = WolverineTracing.ActivitySource.StartActivity(WolverineTracing.StoppingListener);
            activity?.SetTag(WolverineTracing.EndpointAddress, Uri);

            await listener.StopAsync();

            // When called during normal shutdown, latch BEFORE drain so DrainAsync knows
            // it can safely wait for in-flight messages to complete.
            // When called from within the handler pipeline (e.g. PauseListenerContinuation),
            // do NOT latch here: DrainAsync will see _latched==false and return immediately,
            // preventing a deadlock with the current message's execute frame.
            if (latchBeforeDrain)
            {
                LatchReceiver();
            }

            Listener = null;
            _receiver = null;

            if (receiver != null)
            {
                await receiver.DrainAsync();
            }

            try
            {
                await listener.DisposeAsync();
            }
            catch (ObjectDisposedException)
            {
                // Listener may already be disposed during rapid pause/stop cycles.
            }

            receiver?.Dispose();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Unable to stop and drain the listener for {Uri}", Uri);
        }

        Status = ListeningStatus.Stopped;
        _runtime.Tracker.Publish(new ListenerState(Uri, Endpoint.EndpointName, Status));

        _logger.LogInformation("Stopped message listener at {Uri}", Uri);
    }

    public async ValueTask LatchPermanently()
    {
        await StopAndDrainAsync();

        Status = ListeningStatus.GloballyLatched;
        _runtime.Tracker.Publish(new ListenerState(Uri, Endpoint.EndpointName, Status));

        _logger.LogInformation("Listener at {Uri} has been permanently latched", Uri);
        await _runtime.Observer.ListenerLatched(Endpoint);
    }

    public async ValueTask RestartAsync(bool force = true)
    {
        if (force)
        {
            // Tear the listener down even when Status still reports Accepting — the underlying transport channel
            // may be dead while the orchestration status is stale (the #3171-class state, or anything the framework
            // can't self-heal). StopAndDrainAsync sets Status to Stopped, so the StartAsync() below is no longer a
            // no-op and fully rebuilds the listener.
            await StopAndDrainAsync();
        }

        await StartAsync();
    }

    public async ValueTask StartAsync()
    {
        if (Status == ListeningStatus.Accepting)
        {
            return;
        }

        if (_runtime.DurabilitySettings.Mode == DurabilityMode.Balanced
            && _runtime.Restrictions.FindPausedAgentUris().Any(u => u == Uri))
        {
            Status = ListeningStatus.GloballyLatched;
            _runtime.Tracker.Publish(new ListenerState(Uri, Endpoint.EndpointName, Status));
            _logger.LogInformation(
                "Listener at {Uri} is not being started because of an existing agent restriction", Uri);
            return;
        }

        // CritterWatch#942 — never re-attach a listener to a terminally faulted receiver. A faulted
        // block (jasperfx#506) rejects every post and its QueueCount freezes, so reusing it turns a
        // restart into a zombie: the receive loop polls, fails to enqueue, and (for brokers that
        // settle at receipt) drops messages, forever. Rebuild instead.
        if (_receiver is IFaultTrackingReceiver { HasFaulted: true } faulted)
        {
            _logger.LogWarning(
                "The receiver for {Uri} had terminally faulted and is being rebuilt before the listener restarts",
                Uri);
            _receiver = null;
            try
            {
                if (faulted is IAsyncDisposable disposable)
                {
                    await disposable.DisposeAsync();
                }
                else
                {
                    ((IDisposable)faulted).Dispose();
                }
            }
            catch (Exception e)
            {
                _logger.LogInformation(e, "Error disposing the faulted receiver for {Uri}", Uri);
            }
        }

        _receiver ??= Endpoint.MaybeWrapReceiver(await buildReceiverAsync());

        // If this endpoint is part of a global partitioned topology, swap receiver to bridge to local queue
        if (Endpoint.GlobalPartitionLocalQueueUri != null)
        {
            var localQueue = _runtime.Endpoints.AgentForLocalQueue(Endpoint.GlobalPartitionLocalQueueUri) as ILocalQueue;
            if (localQueue != null)
            {
                _receiver = new GlobalPartitionedReceiverBridge(localQueue);
            }
        }
        // If there are global partitioned topologies and this is NOT a paired endpoint, intercept matching messages
        else if (_runtime.Options.MessagePartitioning.GlobalPartitionedTopologies.Count > 0
                 && !Endpoint.UsedInShardedTopology
                 && Endpoint.Uri.Scheme != "local")
        {
            _receiver = new GlobalPartitionedInterceptor(_receiver, _runtime);
        }

        if (Endpoint.ListenerCount > 1)
        {
            var listeners = new List<IListener>(Endpoint.ListenerCount);
            for (var i = 0; i < Endpoint.ListenerCount; i++)
            {
                var listener = await Endpoint.BuildListenerAsync(_runtime, _receiver);
                listeners.Add(listener);
            }

            Listener = new ParallelListener(Uri, listeners);
        }
        else
        {
            Listener = await Endpoint.BuildListenerAsync(_runtime, _receiver);
        }

        Status = ListeningStatus.Accepting;
        _runtime.Tracker.Publish(new ListenerState(Uri, Endpoint.EndpointName, Status));

        _logger.LogInformation("Started message listening at {Uri}", Uri);

        startInboxRecoveryIfNecessary();
    }

    /// <summary>
    /// GH-3590: a durable listener that is only ever active on a single node (Exclusive or PinnedToLeader) can
    /// not rely on the per-database durability agent to recover its dormant inbox messages, because that agent
    /// is assigned per database and routinely lands on a different node. Such a listener owns its own inbox
    /// recovery for as long as it is the active listener.
    /// </summary>
    private void startInboxRecoveryIfNecessary()
    {
        if (Endpoint.Mode != EndpointMode.Durable) return;
        if (!Endpoint.IsSingleNodeListener) return;
        if (!_runtime.Options.Durability.DurabilityAgentEnabled) return;
        if (_runtime.Storage is NullMessageStore) return;

        _inboxRecovery?.SafeDispose();
        _inboxRecovery = new ListenerInboxRecoveryLoop(_runtime, this, _logger);
    }

    private void stopInboxRecovery()
    {
        _inboxRecovery?.SafeDispose();
        _inboxRecovery = null;
    }

    public async ValueTask PauseAsync(TimeSpan pauseTime)
    {
        // Do NOT pre-latch the receiver here. PauseAsync may be called from within the
        // handler pipeline (e.g. via RateLimitContinuation → PauseListenerContinuation).
        // Pre-latching causes DrainAsync to wait for the ActionBlock to drain, which
        // deadlocks because the current message's execute frame is still on the call stack.
        await PauseCoreAsync(pauseTime, latchBeforeDrain: false);
    }

    public async ValueTask PauseWithDrainAsync(TimeSpan pauseTime)
    {
        // Safe to fully drain here: this method is called from background threads
        // (circuit breaker), never from within the handler pipeline call stack.
        await PauseCoreAsync(pauseTime, latchBeforeDrain: true);
    }

    private async ValueTask PauseCoreAsync(TimeSpan pauseTime, bool latchBeforeDrain)
    {
        try
        {
            await StopAndDrainCoreAsync(latchBeforeDrain);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Unable to drain outstanding messages in the listener for {Uri}", Uri);
        }

        _circuitBreaker?.Reset();

        // GH-3832 — a deliberate pause is not the same state as merely stopped, and not the same
        // as a back-pressure TooBusy latch. Both recover on their own, but on different triggers:
        // this one on the Restarter installed below, TooBusy only once the queue drains. Keeping
        // them distinct is what lets BackPressureAgent leave a paused listener alone.
        Status = ListeningStatus.Paused;

        _logger.LogInformation("Pausing message listening at {Uri}", Uri);
        _runtime.Tracker.Publish(new ListenerState(Uri, Endpoint.EndpointName, ListeningStatus.Paused));
        _restarter = new Restarter(this, pauseTime);
    }

    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async ValueTask PauseForInboxRecoveryAsync()
    {
        if (Status != ListeningStatus.Accepting || Listener == null) return;

        await _semaphore.WaitAsync();
        if (Status != ListeningStatus.Accepting || Listener == null)
        {
            _semaphore.Release();
            return;
        }

        try
        {
            await StopAndDrainAsync();
            _circuitBreaker?.Reset();
            _logger.LogWarning("Paused listener at {Uri} — inbox database unavailable", Uri);
            _runtime.Tracker.Publish(new ListenerState(Uri, Endpoint.EndpointName, ListeningStatus.Stopped));

            _restarter?.SafeDispose();
            _restarter = new InboxHealthRestarter(this, _runtime, _logger);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async ValueTask MarkAsTooBusyAndStopReceivingAsync()
    {
        if (Status != ListeningStatus.Accepting || Listener == null)
        {
            return;
        }

        await _semaphore.WaitAsync();
        if (Status != ListeningStatus.Accepting || Listener == null)
        {
            _semaphore.Release();
            return;
        }

        try
        {
            using var activity = WolverineTracing.ActivitySource.StartActivity(WolverineTracing.PausingListener);
            activity?.SetTag(WolverineTracing.EndpointAddress, Listener.Address);
            activity?.SetTag(WolverineTracing.StopReason, WolverineTracing.TooBusy);

            try
            {
                await Listener.StopAsync();
                await Listener.DisposeAsync();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Unable to cleanly stop the listener for {Uri}", Uri);
            }
        
            Listener = null;

            Status = ListeningStatus.TooBusy;
            _runtime.Tracker.Publish(new ListenerState(Uri, Endpoint.EndpointName, Status));

            _logger.LogInformation("Marked listener at {Uri} as too busy and stopped receiving. The current local message count is {LocalCount}, and the BufferingLimits are set to {BufferingLimits}. You may want to increase the buffering limits", Uri, QueueCount, Endpoint.BufferingLimits);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async ValueTask<IReceiver> buildReceiverAsync()
    {
        switch (Endpoint.Mode)
        {
            case EndpointMode.Durable:
                var receiver = new DurableReceiver(Endpoint, _runtime, _pipeline);
                await receiver.ClearInFlightIncomingAsync();
                return receiver;

            case EndpointMode.Inline:
                return new InlineReceiver(Endpoint, _runtime, _pipeline);

            case EndpointMode.BufferedInMemory:
                return new BufferedReceiver(Endpoint, _runtime, _pipeline);

            case EndpointMode.NativeAck:
                return new NativeAckReceiver(Endpoint, _runtime, _pipeline);

            default:
                throw new ArgumentOutOfRangeException(nameof(Endpoint.Mode), Endpoint.Mode,
                    $"Unknown {nameof(EndpointMode)} for the listening endpoint at {Endpoint.Uri}");
        }
    }
}

internal class Restarter : IDisposable
{
    private readonly CancellationTokenSource _cancellation;
    private readonly Task<Task> _task;

    public Restarter(IListenerCircuit parent, TimeSpan timeSpan)
    {
        _cancellation = new CancellationTokenSource();
        _task = Task.Delay(timeSpan, _cancellation.Token)
            .ContinueWith(async _ =>
            {
                if (_cancellation.IsCancellationRequested)
                {
                    return;
                }

                await parent.StartAsync();
            }, TaskScheduler.Default);
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
        _task.SafeDispose();
    }
}

internal class InboxHealthRestarter : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _task;

    public InboxHealthRestarter(IListenerCircuit parent, IWolverineRuntime runtime, ILogger logger)
    {
        _task = Task.Run(() => ProbeLoopAsync(parent, runtime, logger, _cancellation.Token));
    }

    private static async Task ProbeLoopAsync(
        IListenerCircuit parent, IWolverineRuntime runtime, ILogger logger, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        var maxDelay = TimeSpan.FromSeconds(30);

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return; }

            try
            {
                // Lightweight probe — releases 0 rows but exercises DB connection
                await runtime.Storage.Inbox.ReleaseIncomingAsync(0, new Uri("wolverine://inbox-health-probe"));

                logger.LogInformation("Inbox available again for {Uri}. Restarting listener.", parent.Endpoint.Uri);
                await parent.StartAsync();
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Inbox still unavailable for {Uri}. Retrying in {Delay}.", parent.Endpoint.Uri, delay);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, maxDelay.TotalMilliseconds));
            }
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
        _task.SafeDispose();
    }
}

internal class RetryOnInlineChannelCallback : IListener
{
    private readonly IListener _inner;
    private readonly IWolverineRuntime _runtime;

    public RetryOnInlineChannelCallback(IListener inner, IWolverineRuntime runtime)
    {
        _inner = inner;
        _runtime = runtime;
    }

    public IHandlerPipeline? Pipeline => _inner.Pipeline;
    public async ValueTask CompleteAsync(Envelope envelope)
    {
        try
        {
            await _runtime.Storage.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);
        }
        catch (Exception e)
        {
            _runtime.Logger.LogError(e, "Error trying to mark a message as handled in the transactional inbox");
        }

        await _inner.CompleteAsync(envelope);
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        return _inner.DeferAsync(envelope);
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask();
    }

    public Uri Address => _inner.Address;
    public ValueTask StopAsync()
    {
        return new ValueTask();
    }
}
