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
internal class NativeAckReceiver : IReceiver, IFaultTrackingReceiver
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

    public NativeAckReceiver(Endpoint endpoint, IWolverineRuntime runtime, IHandlerPipeline pipeline)
    {
        _endpoint = endpoint;
        Uri = endpoint.Uri;
        _logger = runtime.LoggerFactory.CreateLogger<NativeAckReceiver>();
        _settings = runtime.DurabilitySettings;
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

    public void Dispose()
    {
        _receivingBlock.Complete();
        _completeBlock.Dispose();
        _deferBlock.Dispose();
    }

    public async ValueTask ReceivedAsync(IListener listener, Envelope[] messages)
    {
        if (messages.Length == 0) return;

        if (_settings.Cancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException();
        }

        var now = DateTimeOffset.Now;

        foreach (var envelope in messages)
        {
            await receiveOneAsync(listener, envelope, now).ConfigureAwait(false);
        }

        _logger.IncomingBatchReceived(Uri, messages);
    }

    public async ValueTask ReceivedAsync(IListener listener, Envelope envelope)
    {
        await receiveOneAsync(listener, envelope, DateTimeOffset.Now).ConfigureAwait(false);
        _logger.IncomingReceived(envelope, Uri);
    }

    private async ValueTask receiveOneAsync(IListener listener, Envelope envelope, DateTimeOffset now)
    {
        if (envelope.IsPing()) return;

        envelope.MarkReceived(listener, now, _settings, _endpoint.WireTap);

        if (envelope.IsExpired())
        {
            // Nothing will ever handle it, so settle it rather than leaving the broker to redeliver forever.
            await _completeBlock.PostAsync(envelope).ConfigureAwait(false);
            return;
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
            return;
        }

        var activity = _endpoint.TelemetryEnabled ? WolverineTracing.StartReceiving(envelope) : null;

        // NOTE the absence of a _completeBlock.PostAsync here. That single line is what separates this mode
        // from BufferedInMemory: the delivery stays unacknowledged until the pipeline settles it.
        await _receivingBlock.PostAsync(envelope).ConfigureAwait(false);

        activity?.Stop();
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

            // The pipeline did not settle it, so the broker still owns it and will redeliver. Nack rather
            // than leaving the delivery dangling until the connection drops, and forget the id so the
            // redelivery is allowed to run.
            _idempotency?.Release(envelope);

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
