using System.Diagnostics;
using System.Diagnostics.Metrics;
using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Runtime.Partitioning;
using Wolverine.Transports;

namespace Wolverine.Runtime.WorkerQueues;

/// <summary>
/// GH-3708. The receiver for <see cref="EndpointMode.NativeAck"/>: BufferedReceiver's execution block with
/// Inline's channel wiring. An incoming delivery is enqueued into an in-memory (optionally group-partitioned)
/// block and <b>deliberately not settled</b>; the broker delivery stays unacknowledged until the handler
/// pipeline reaches a terminal, at which point the completion continuation settles it natively against the
/// listener -- ack on success, nack or dead-letter on terminal failure.
///
/// <para>
/// Two consequences follow from never acking at receipt, and both are the point rather than side effects:
/// </para>
/// <list type="bullet">
/// <item>There is no loss window. Anything queued but not yet completed is still unacknowledged, so a crash
/// or a closed channel makes the broker redeliver it. That is also why <see cref="DrainAsync"/> does not need
/// to guarantee it finishes -- whatever it cannot drain in time is simply never settled.</item>
/// <item>Back pressure is the broker's prefetch window rather than an in-process BackPressureAgent: the broker
/// stops delivering once its unacked ceiling is reached. See <see cref="Endpoint.ShouldEnforceBackPressure"/>.</item>
/// </list>
///
/// <para>
/// Scheduling and dead-lettering are the <i>listener's</i> capabilities in this mode, not this receiver's,
/// because the pipeline channel is the listener -- exactly as in <see cref="InlineReceiver"/>. This receiver
/// deliberately does not implement ISupportNativeScheduling or ISupportDeadLetterQueue; doing so would route
/// those terminals through an in-memory structure whose contents a crash would lose, which is the guarantee
/// this mode exists to provide.
/// </para>
/// </summary>
internal class NativeAckReceiver : IReceiver, IFaultTrackingReceiver, ILatchedReceiver
{
    private readonly RetryBlock<Envelope> _completeBlock;
    private readonly RetryBlock<Envelope> _deferBlock;
    private readonly Endpoint _endpoint;
    private readonly ILogger _logger;
    private readonly IBlock<Envelope> _receivingBlock;
    private readonly DurabilitySettings _settings;

    /// <summary>
    /// GH-3710. Null unless the endpoint opted in with WithInMemoryIdempotency(), which is what makes the
    /// guard zero-cost by default: one null check per delivery and nothing else. Owned by the endpoint rather
    /// than by this receiver, so its contents survive the receiver rebuilds -- back pressure recovery, listener
    /// restart -- that are themselves a source of redeliveries.
    /// </summary>
    private readonly IIncomingIdempotencyGuard? _idempotency;

    private bool _latched;

    // GH-4048. Lazily built on the first delivery that arrives on a lease-renewing listener -- the listener does
    // not exist yet at construction time, because ListeningAgent builds the receiver first and hands it to the
    // listener. Null forever on a transport whose unsettled deliveries never expire (RabbitMQ, Redis Streams).
    private readonly object _leaseGate = new();
    private readonly Meter? _meter;
    private LeaseRenewalTracker? _leases;

    public NativeAckReceiver(Endpoint endpoint, IWolverineRuntime runtime, IHandlerPipeline pipeline)
    {
        _endpoint = endpoint;
        Uri = endpoint.Uri;
        _logger = runtime.LoggerFactory.CreateLogger<NativeAckReceiver>();
        _settings = runtime.DurabilitySettings;
        _meter = runtime.Meter;
        _idempotency = endpoint.IdempotencyGuard;
        Pipeline = pipeline;

        // Guard against a listener-less envelope reaching either retry block -- env.Listener is null for the
        // all-zero system handshake envelope, and an unguarded deref NREs into the retry loop. GH-3013.
        _deferBlock = new RetryBlock<Envelope>(
            (env, _) => env.Listener is { } l ? l.DeferAsync(env).AsTask() : Task.CompletedTask, runtime.Logger,
            runtime.Cancellation);

        // Unlike BufferedReceiver, this block is NOT posted to on receipt. It exists only for envelopes that
        // never reach the handler pipeline at all -- an already-expired delivery, which has to be acked so the
        // broker stops redelivering something nobody will ever process.
        _completeBlock = new RetryBlock<Envelope>(
            (env, _) => env.Listener is { } l ? l.CompleteAsync(env).AsTask() : Task.CompletedTask, runtime.Logger,
            runtime.Cancellation);

        if (endpoint.GroupShardingSlotNumber == null)
        {
            _receivingBlock = new Block<Envelope>(endpoint.MaxDegreeOfParallelism,
                Block<Envelope>.DefaultBoundedCapacity, executeAsync);
        }
        else
        {
            var sharded = new ShardedExecutionBlock((int)endpoint.GroupShardingSlotNumber,
                runtime.Options.MessagePartitioning, Block<Envelope>.DefaultBoundedCapacity, executeAsync,
                // GH-3899: message types exempted from partitioned processing run at the endpoint's normal
                // parallelism instead of a sequential GroupId slot
                endpoint.MaxDegreeOfParallelism);
            sharded.OnError = onBlockError;

            // GH-4013 built this overload for exactly this mode: the channel has to be resolved PER ENVELOPE,
            // because each delivery settles against the listener that delivered it. Binding a single
            // IChannelCallback for the whole block -- which is all BufferedReceiver ever needed, since its
            // completions are no-ops -- would ack the wrong delivery under ListenerCount > 1.
            _receivingBlock = sharded.DeserializeFirst(pipeline, runtime, channelFor);
        }

        _receivingBlock.OnError = onBlockError;
    }

    public Uri Uri { get; }

    public IHandlerPipeline Pipeline { get; }

    public int QueueCount => (int)_receivingBlock.Count;

    /// <summary>CritterWatch#942 -- set when the receiving block faults terminally (jasperfx#506).</summary>
    public bool HasFaulted { get; private set; }

    /// <summary>
    /// GH-4048. The lease renewal tracker, if this endpoint's listener has one. Null until the first delivery
    /// arrives on a lease-renewing listener, and forever on a transport whose deliveries do not expire.
    /// </summary>
    internal LeaseRenewalTracker? Leases => _leases;

    public void Dispose()
    {
        _receivingBlock.Complete();
        _completeBlock.Dispose();
        _deferBlock.Dispose();

        // Fire and forget: the tracker's loop exits on its own cancellation and bounds its own wait, and
        // IReceiver.Dispose is synchronous.
        var leases = Interlocked.Exchange(ref _leases, null);
        if (leases != null)
        {
            _ = leases.DisposeAsync().AsTask();
        }
    }

    public async ValueTask ReceivedAsync(IListener listener, Envelope[] messages)
    {
        if (messages.Length == 0) return;

        if (_settings.Cancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException();
        }

        var now = DateTimeOffset.Now;

        // GH-4091. TWO passes, deliberately. Posting blocks once the execution block is at capacity, so a
        // single admit-then-post loop leaves every envelope after the first blocked one untracked -- with its
        // broker lease already running -- for as long as the ones ahead of it take to be admitted. Tracking
        // the whole batch first closes that window. The exposure was bounded by batch size rather than lane
        // depth, but it opened precisely when the block was full, which is the flood this mode exists for.
        var admitted = new List<(Envelope Envelope, Activity? Activity)>(messages.Length);

        foreach (var envelope in messages)
        {
            if (await admitAsync(listener, envelope, now).ConfigureAwait(false) is { } entry)
            {
                admitted.Add(entry);
            }
        }

        await postAllAsync(admitted).ConfigureAwait(false);

        _logger.IncomingBatchReceived(Uri, messages);
    }

    public async ValueTask ReceivedAsync(IListener listener, Envelope envelope)
    {
        if (await admitAsync(listener, envelope, DateTimeOffset.Now).ConfigureAwait(false) is { } entry)
        {
            await postAllAsync([entry]).ConfigureAwait(false);
        }

        _logger.IncomingReceived(envelope, Uri);
    }

    /// <summary>
    /// Everything that happens to a delivery BEFORE it is posted to the execution block: the terminal cases
    /// that never reach a lane at all, and -- for the ones that do -- registering the lease. Returns null when
    /// the envelope is not going any further.
    /// </summary>
    private async ValueTask<(Envelope Envelope, Activity? Activity)?> admitAsync(IListener listener,
        Envelope envelope, DateTimeOffset now)
    {
        if (envelope.IsPing()) return null;

        envelope.MarkReceived(listener, now, _settings, _endpoint.WireTap);

        if (envelope.IsExpired())
        {
            // Nothing will ever handle it, so settle it rather than leaving the broker to redeliver forever.
            await _completeBlock.PostAsync(envelope).ConfigureAwait(false);
            return null;
        }

        // GH-3710. Ack-and-drop a redelivery of something this process already handled (or is handling right
        // now), which is exactly what DurableReceiver.handleDuplicateIncomingEnvelope does when the inbox
        // INSERT hits the primary key -- minus the database. Debug rather than Error, unlike the durable path:
        // in a mode that never settles on receipt, redelivery after a rolling deploy is expected operational
        // noise rather than a sign that something went wrong.
        if (_idempotency != null && !_idempotency.TryBeginProcessing(envelope))
        {
            _logger.LogDebug(
                "Discarding duplicate delivery of envelope {EnvelopeId} ({MessageType}) at {Uri}; it was already handled within the in-memory idempotency window",
                envelope.Id, envelope.MessageType, Uri);

            await _completeBlock.PostAsync(envelope).ConfigureAwait(false);
            return null;
        }

        var activity = _endpoint.TelemetryEnabled ? WolverineTracing.StartReceiving(envelope) : null;

        // GH-4048. The risk window opens HERE, not when a handler starts: from this point the delivery is held
        // unsettled for lane queue time plus handler time, and on a clocked transport that clock is already
        // running. Untracked in executeAsync's finally.
        leasesFor(listener)?.Track(envelope);

        return (envelope, activity);
    }

    /// <summary>
    /// The second pass. NOTE the absence of a <c>_completeBlock.PostAsync</c> here -- that single omission is
    /// what separates this mode from BufferedInMemory: the delivery stays unacknowledged until the pipeline
    /// settles it.
    /// </summary>
    private async ValueTask postAllAsync(IReadOnlyList<(Envelope Envelope, Activity? Activity)> admitted)
    {
        for (var i = 0; i < admitted.Count; i++)
        {
            var (envelope, activity) = admitted[i];

            try
            {
                await _receivingBlock.PostAsync(envelope).ConfigureAwait(false);
            }
            catch
            {
                // GH-4091. Tracking ahead of posting means a failed post -- a completed block during a drain,
                // most likely -- leaves envelopes registered that executeAsync will now never untrack. This one
                // and everything behind it are in that position, so release them here. The deliveries stay
                // unsettled, which is the mode's whole point: the broker redelivers them.
                for (var j = i; j < admitted.Count; j++)
                {
                    var (stranded, strandedActivity) = admitted[j];
                    _leases?.Untrack(stranded);
                    _idempotency?.Release(stranded);
                    strandedActivity?.Stop();
                }

                throw;
            }

            activity?.Stop();
        }
    }

    private LeaseRenewalTracker? leasesFor(IListener listener)
    {
        var existing = _leases;
        if (existing != null) return existing;

        if (listener is not ISupportLeaseRenewal renewal) return null;

        lock (_leaseGate)
        {
            // Every listener built for one endpoint reads the same configuration, so the durations only need
            // to be read once; the renewal call still goes to whichever listener delivered the envelope.
            return _leases ??= new LeaseRenewalTracker(renewal, Uri, _logger, _meter, _settings.Cancellation);
        }
    }

    internal async Task executeAsync(Envelope envelope, CancellationToken _)
    {
        if (_latched && envelope.Listener != null)
        {
            // Latched before this one started: hand it straight back to the broker instead of holding an
            // unacked delivery that nothing is going to process. The broker WILL redeliver it, so the guard
            // has to forget the id or the redelivery would be dropped as a duplicate of a message that never ran.
            _idempotency?.Release(envelope);
            await _deferBlock.PostAsync(envelope).ConfigureAwait(false);
            return;
        }

        // GH-4048. Sits deliberately NEXT TO the latched branch above and behaves in the opposite way. Latched
        // still holds the lease, so handing the delivery back is correct. A lost lease does not: the broker owns
        // it again and is redelivering it. Every transport's defer path is settle-then-republish -- SQS's requeue
        // block deletes and re-sends, ASB's completes and re-sends, Pub/Sub's republishes -- so deferring here
        // would put a SECOND copy on the queue on top of the redelivery. Completing is no better: a settle with a
        // dead handle is either silently ignored (SQS) or a permanent failure inside a RetryBlock (ASB).
        // Dropping is the only non-amplifying option, and it turns "handled twice" into "handled once, later".
        if (_leases is { } leases && !leases.TryBeginExecution(envelope))
        {
            leases.Untrack(envelope);
            _logger.LogDebug(
                "Dropping envelope {EnvelopeId} from the native-ack lane at {Uri} without settling it; its broker lease was lost before it started executing",
                envelope.Id, Uri);
            return;
        }

        try
        {
            if (envelope.ContentType.IsEmpty())
            {
                envelope.ContentType = EnvelopeConstants.JsonContentType;
            }

            // The channel is the LISTENER, which is what makes every terminal settle natively.
            await Pipeline.InvokeAsync(envelope, channelFor(envelope)).ConfigureAwait(false);

            // GH-3710. Envelope.HasBeenAcked is the precise question the guard needs answered: was this
            // delivery settled in a way the broker will not undo? Success acks it through
            // MessageContext.CompleteAsync, and so does a native dead-letter move. A requeue or a nack does
            // NOT set it, and those are the cases where remembering the id would turn a retry into a lost
            // message -- so anything else releases.
            recordOutcome(envelope);
        }
        catch (Exception e)
        {
            try
            {
                _logger.LogError(e, "Unexpected error in Pipeline invocation for {Uri}", Uri);
            }
            catch
            {
                // CritterWatch#942 -- an exception escaping here faults the block terminally via Block's error
                // rung, and a faulted block never recovers. Swallow: the message fails, the listener survives.
            }

            // GH-3710. The pipeline did not settle it, so the broker still owns it and will redeliver. Forget
            // the id so that redelivery is allowed to run -- unconditionally, because it is equally true when
            // the nack below is suppressed: a lost lease means the broker is redelivering anyway, and a
            // remembered id would suppress the very attempt that is supposed to replace this one.
            _idempotency?.Release(envelope);

            // GH-4048. The nack is suppressed when the lease was lost mid-execution. The broker is already
            // redelivering this one, and every defer path settles-then-republishes, so a nack here would add a
            // second copy on top of it. This envelope stays at-least-once -- a running handler cannot be
            // un-run -- but the duplication is not compounded, and the tracker has already metered it as
            // lost-while-executing.
            if (_leases?.WasLeaseLost(envelope) == true)
            {
                _logger.LogDebug(
                    "Not deferring envelope {EnvelopeId} at {Uri} after a failed pipeline invocation; its broker lease was lost while it was executing, so the broker already owns the delivery",
                    envelope.Id, Uri);
            }
            else
            {
                // The pipeline did not settle it, so the broker still owns it. Nack rather than leaving the
                // delivery dangling until the connection drops.
                try
                {
                    await _deferBlock.PostAsync(envelope).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error trying to defer envelope {EnvelopeId} back to the broker", envelope.Id);
                }
            }
        }
        finally
        {
            // The risk window closes here: whatever the terminal was, this envelope is no longer waiting in a
            // lane on a lease Wolverine has to keep alive.
            _leases?.Untrack(envelope);
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

    private IChannelCallback channelFor(Envelope envelope)
    {
        // Envelope.Listener is assigned by MarkReceived on every path into this receiver.
        return envelope.Listener is { } listener ? listener : NullChannelCallback.Instance;
    }

    public void Latch()
    {
        _latched = true;
    }

    public async ValueTask DrainAsync()
    {
        // Same re-entrancy guard as the other receivers: a drain triggered from INSIDE the pipeline (a rate
        // limiting pause, say) must not wait on the block, because the current message's execute function is
        // still on the call stack and would deadlock.
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

        // Deliberately NOT a correctness requirement that the block emptied. Anything still queued was never
        // acked, so closing the channel hands it back to the broker. The cost is redelivery -- bounded by the
        // prefetch window -- not loss. Quantifying that duplicate count under a rolling deploy is GH-3713.
        await _completeBlock.DrainAsync().ConfigureAwait(false);
        await _deferBlock.DrainAsync().ConfigureAwait(false);

        // GH-4048. Nothing left to keep alive: whatever this drain could not process is unacked, so the broker
        // gets it back on its own clock.
        var leases = Interlocked.Exchange(ref _leases, null);
        if (leases != null)
        {
            await leases.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void onBlockError(Envelope? envelope, Exception ex)
    {
        if (envelope == null)
        {
            HasFaulted = true;
            try
            {
                _logger.LogCritical(ex,
                    "The native-ack worker queue for {Uri} has faulted and stopped processing. Messages already delivered but not yet settled will be redelivered by the broker",
                    Uri);
            }
            catch
            {
                // Flag is already set; recovery does not depend on this line landing.
            }

            return;
        }

        try
        {
            _logger.LogError(ex, "Error processing envelope {EnvelopeId} ({MessageType}) in the native-ack queue for {Uri}",
                envelope.Id, envelope.MessageType, Uri);
        }
        catch
        {
            // Swallow: an escape here would fault the block terminally.
        }
    }
}

/// <summary>
/// GH-3708. Stand-in for the vanishingly rare envelope that reaches the native-ack receiver without a listener
/// attached. Settling is a no-op because there is no broker delivery to settle.
/// </summary>
internal class NullChannelCallback : IChannelCallback
{
    internal static readonly NullChannelCallback Instance = new();

    public IHandlerPipeline? Pipeline => null;

    public ValueTask CompleteAsync(Envelope envelope)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        return ValueTask.CompletedTask;
    }
}
