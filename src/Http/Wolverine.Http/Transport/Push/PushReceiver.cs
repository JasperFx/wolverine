using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Transports;

namespace Wolverine.Http.Transport.Push;

/// <summary>
///     GH-5016. What became of one pushed message, and so what the broker should be told
/// </summary>
public enum PushReceiveOutcome
{
    /// <summary>
    ///     Handled, discarded or moved to dead letter storage. Answer with a success status so the broker acks it
    /// </summary>
    Acknowledged,

    /// <summary>
    ///     The inbox already holds this message id as handled, so it was not handled again. Ack it
    /// </summary>
    Duplicate,

    /// <summary>
    ///     Not handled. Answer with a failure status so the broker redelivers it with its own backoff and, in the
    ///     end, its own dead letter topic
    /// </summary>
    Retry
}

/// <summary>
///     GH-5016. Handles one pushed message inline, inside the request, for a serverless deployment where nothing can
///     be trusted to run after the response is written. The handler's error policies decide the outcome: success,
///     discard and "move to error queue" (Wolverine's dead letter storage) all ack, while a requeue, a scheduled retry
///     or an exception that escapes the policies asks the broker to redeliver. Outgoing messages are sent with
///     <see cref="MessageContext.SendAllOutgoingMessagesInlineAsync" /> before the outcome is returned.
/// </summary>
/// <remarks>
///     <see cref="PushReceiverEndpointExtensions" /> maps this onto an ASP.NET Core route. A hand-written function
///     entry point can use it directly with an envelope it has mapped itself.
/// </remarks>
public class PushReceiver : IHandlerPipeline
{
    private readonly WolverineRuntime _runtime;
    private readonly ILogger<PushReceiver> _logger;
    private readonly LightweightCache<Type, IExecutor> _executors;

    public PushReceiver(IWolverineRuntime runtime, Uri address, bool deduplicateWithInbox = false)
    {
        _runtime = (WolverineRuntime)runtime;
        _logger = runtime.LoggerFactory.CreateLogger<PushReceiver>();
        _executors = new LightweightCache<Type, IExecutor>(type => ((IExecutorFactory)_runtime).BuildFor(type));
        Address = address;
        DeduplicateWithInbox = deduplicateWithInbox;
    }

    /// <summary>
    ///     Stamped onto every received envelope that does not name a destination of its own. This is the
    ///     <c>received_at</c> of its inbox and dead letter rows
    /// </summary>
    public Uri Address { get; }

    /// <summary>
    ///     Skip a message whose id the durable inbox already holds, and record each acked message there as handled
    ///     so a later redelivery of it is skipped. The rows expire after
    ///     <see cref="DurabilitySettings.KeepAfterMessageHandling" />
    /// </summary>
    public bool DeduplicateWithInbox { get; }

    /// <summary>
    ///     Handle one pushed message inline and say what the broker should be told
    /// </summary>
    public async Task<PushReceiveOutcome> ReceiveAsync(Envelope envelope, CancellationToken cancellation)
    {
        envelope.Destination ??= Address;

        // There is no listening endpoint to supply the serializer, so go by the content type
        if (envelope.Message == null && envelope.Serializer == null)
        {
            envelope.Serializer = _runtime.Options.DetermineSerializer(envelope);
        }

        if (DeduplicateWithInbox && await _runtime.Storage.Inbox.ExistsAsync(envelope, cancellation).ConfigureAwait(false))
        {
            _logger.LogInformation("Skipping pushed message {EnvelopeId} ({MessageType}) received at {Address} because the inbox has already handled it",
                envelope.Id, envelope.MessageType, Address);
            return PushReceiveOutcome.Duplicate;
        }

        var channel = new PushChannelCallback(this);
        await invokeAsync(envelope, channel, cancellation).ConfigureAwait(false);

        if (channel.Outcome == PushReceiveOutcome.Acknowledged && DeduplicateWithInbox)
        {
            await recordHandledAsync(envelope).ConfigureAwait(false);
        }

        return channel.Outcome;
    }

    private async Task recordHandledAsync(Envelope envelope)
    {
        var handled = Envelope.ForPersistedHandled(envelope, DateTimeOffset.UtcNow, _runtime.Options.Durability);
        try
        {
            await _runtime.Storage.Inbox.StoreIncomingAsync(handled).ConfigureAwait(false);
        }
        catch (DuplicateIncomingEnvelopeException)
        {
            // A concurrent redelivery of the same message got there first. It is recorded either way
        }
        catch (Exception e)
        {
            // The message itself was handled and must still be acked, so this costs at most a duplicate later
            _logger.LogError(e, "Unable to record pushed message {EnvelopeId} as handled in the inbox. A redelivery of it would be handled again",
                envelope.Id);
        }
    }

    // Retries from the error policies (RetryNow, RetryWithCooldown) come back through here by way of the
    // channel's Pipeline, so they get the same inline flush as the first attempt
    Task IHandlerPipeline.InvokeAsync(Envelope envelope, IChannelCallback channel)
    {
        return invokeAsync(envelope, (PushChannelCallback)channel, _runtime.Cancellation);
    }

    Task IHandlerPipeline.InvokeAsync(Envelope envelope, IChannelCallback channel, Activity activity)
    {
        return invokeAsync(envelope, (PushChannelCallback)channel, _runtime.Cancellation);
    }

    ValueTask<IContinuation> IHandlerPipeline.TryDeserializeEnvelope(Envelope envelope)
    {
        return _runtime.Pipeline.TryDeserializeEnvelope(envelope);
    }

    private async Task invokeAsync(Envelope envelope, PushChannelCallback channel, CancellationToken cancellation)
    {
        var activity = WolverineTracing.StartExecuting(envelope);
        var context = _runtime.ExecutionPool.Get();
        context.ReadEnvelope(envelope, channel);

        try
        {
            var continuation = await executeAsync(context, envelope, cancellation).ConfigureAwait(false);

            if (continuation is MessageSucceededContinuation)
            {
                // The success path's flush is the inline one, before the ack
                await context.SendAllOutgoingMessagesInlineAsync().ConfigureAwait(false);
                await context.CompleteAsync().ConfigureAwait(false);
                context.CompletionTrackerFor(_runtime).MessageSucceeded(envelope);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            else
            {
                await continuation.ExecuteAsync(context, _runtime, DateTimeOffset.UtcNow, activity).ConfigureAwait(false);

                // A failure acknowledgement or Fault<T> published by the error handling, sent before answering
                await context.SendAllOutgoingMessagesInlineAsync().ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            // Unlike a listener there is no reason to swallow this: the broker still holds the message, so
            // leave it un-acked and let the broker redeliver it
            _logger.LogError(e, "Failure while handling pushed message {EnvelopeId} ({MessageType}) received at {Address}. Asking the broker to redeliver it",
                envelope.Id, envelope.MessageType, Address);
            _runtime.MessageTracking.MessageFailed(envelope, e);
            activity?.SetStatus(ActivityStatusCode.Error, e.GetType().Name);
            channel.Fail();
        }
        finally
        {
            _runtime.ExecutionPool.Return(context);
            activity?.Stop();
        }
    }

    private async ValueTask<IContinuation> executeAsync(MessageContext context, Envelope envelope, CancellationToken cancellation)
    {
        if (envelope.IsExpired())
        {
            return new DiscardEnvelope(new EnvelopeExpiredException(envelope));
        }

        if (envelope.Message == null)
        {
            // Records the receipt as well
            var deserialized = await _runtime.Pipeline.TryDeserializeEnvelope(envelope).ConfigureAwait(false);
            if (deserialized is not NullContinuation) return deserialized;
        }
        else if (!envelope.WasTrackedAsReceived)
        {
            envelope.WasTrackedAsReceived = true;
            _runtime.MessageTracking.Received(envelope);
        }

        // An unhandled message type gets a NoHandlerExecutor, which answers with the missing handler policy
        var executor = _executors[envelope.Message!.GetType()];
        return await executor.ExecuteAsync(context, cancellation).ConfigureAwait(false);
    }
}

/// <summary>
///     Records what the error handling did with a pushed message, in place of a broker's ack and nack
/// </summary>
internal class PushChannelCallback : IChannelCallback, ISupportNativeScheduling
{
    private PushReceiveOutcome? _outcome;

    public PushChannelCallback(IHandlerPipeline pipeline)
    {
        Pipeline = pipeline;
    }

    public IHandlerPipeline? Pipeline { get; }

    // Nothing settled it, which only happens when processing was cut short, so let the broker try again
    public PushReceiveOutcome Outcome => _outcome ?? PushReceiveOutcome.Retry;

    public ValueTask CompleteAsync(Envelope envelope)
    {
        _outcome ??= PushReceiveOutcome.Acknowledged;
        return ValueTask.CompletedTask;
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        _outcome = PushReceiveOutcome.Retry;
        return ValueTask.CompletedTask;
    }

    // A scheduled retry is the broker's to make, with its own backoff
    public bool NativeSchedulingEnabled => true;

    public Task MoveToScheduledUntilAsync(Envelope envelope, DateTimeOffset time)
    {
        _outcome = PushReceiveOutcome.Retry;
        return Task.CompletedTask;
    }

    // Processing blew up before anything settled the message. Once acked it stays acked
    public void Fail()
    {
        _outcome ??= PushReceiveOutcome.Retry;
    }
}
