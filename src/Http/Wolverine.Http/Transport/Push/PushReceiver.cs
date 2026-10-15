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
    ///     Claim each message id in the durable inbox before handling it, so a redelivery that arrives while it is
    ///     still running is answered with the retry status rather than run again, and a redelivery of a message
    ///     that was already acked is acked without running it. The handled rows expire after
    ///     <see cref="DurabilitySettings.KeepAfterMessageHandling" />
    /// </summary>
    /// <remarks>
    ///     GH-5049. The claim is an <see cref="EnvelopeStatus.Incoming" /> inbox row owned by this node, so a
    ///     <see cref="DurabilityMode.ServerlessWithOutbox" /> durability pass leaves it alone until
    ///     <see cref="DurabilitySettings.InboxStaleTime" /> runs out, and only then recovers a message whose process
    ///     died while handling it. It is marked handled when the message is acked, and deleted when the broker is
    ///     asked to redeliver, so that redelivery can claim it again
    /// </remarks>
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

        if (DeduplicateWithInbox && await tryClaimAsync(envelope, cancellation).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var channel = new PushChannelCallback(this);
        try
        {
            await invokeAsync(envelope, channel, cancellation).ConfigureAwait(false);
        }
        finally
        {
            if (DeduplicateWithInbox)
            {
                if (channel.Outcome == PushReceiveOutcome.Acknowledged)
                {
                    await recordHandledAsync(envelope, channel.Succeeded).ConfigureAwait(false);
                }
                else
                {
                    await releaseClaimAsync(envelope).ConfigureAwait(false);
                }
            }
        }

        return channel.Outcome;
    }

    /// <summary>
    ///     Handle every envelope of one pushed request inline, in order, and say what the broker should be told
    ///     about the request as a whole. It is acked only when every envelope was. The first envelope that asks for
    ///     a retry stops the rest, which have not run yet, so the broker's redelivery runs them in order. The
    ///     envelopes before it were handled and will be handled again on that redelivery, unless
    ///     <see cref="DeduplicateWithInbox" /> recognizes them
    /// </summary>
    public async Task<PushReceiveOutcome> ReceiveAsync(IReadOnlyList<Envelope> envelopes, CancellationToken cancellation)
    {
        if (envelopes.Count == 1) return await ReceiveAsync(envelopes[0], cancellation).ConfigureAwait(false);

        var outcome = PushReceiveOutcome.Duplicate;
        for (var i = 0; i < envelopes.Count; i++)
        {
            var received = await ReceiveAsync(envelopes[i], cancellation).ConfigureAwait(false);
            if (received == PushReceiveOutcome.Retry)
            {
                _logger.LogInformation(
                    "Pushed message {EnvelopeId} ({MessageType}) received at {Address} asked for a retry, so the {Remaining} message(s) after it in the same request are left for the broker to redeliver",
                    envelopes[i].Id, envelopes[i].MessageType, Address, envelopes.Count - 1 - i);
                return PushReceiveOutcome.Retry;
            }

            if (received == PushReceiveOutcome.Acknowledged) outcome = PushReceiveOutcome.Acknowledged;
        }

        return outcome;
    }

    /// <summary>
    ///     GH-5049. Claim the message id in the inbox before running the handler, with an Incoming row owned by this
    ///     node. Null when this delivery holds the claim, otherwise what the broker should be told
    /// </summary>
    /// <remarks>
    ///     Owned by this node rather than by any node: a <see cref="DurabilityMode.ServerlessWithOutbox" />
    ///     durability pass recovers an Incoming row owned by any node at once, which would run the message a second
    ///     time while this request is still handling it. A row this node owns is only recovered once
    ///     <see cref="DurabilitySettings.InboxStaleTime" /> runs out, which in that mode has to be longer than the
    ///     longest handler anyway, so the pass only takes the claim of a process that died mid-request. An Incoming
    ///     row rather than a Handled one, so that recovered message still runs, and its body is stored for that
    /// </remarks>
    private async Task<PushReceiveOutcome?> tryClaimAsync(Envelope envelope, CancellationToken cancellation)
    {
        var inbox = _runtime.Storage.Inbox;

        try
        {
            // Checked first because with inbox partitioning a Handled row does not collide with a new Incoming one.
            // A store that cannot tell a handled row from a claim treats any row as handled, as before GH-5049
            var handled = await inbox.TryFindHandledAsync(envelope, cancellation).ConfigureAwait(false)
                          ?? await inbox.ExistsAsync(envelope, cancellation).ConfigureAwait(false);
            if (handled)
            {
                logDuplicate(envelope);
                return PushReceiveOutcome.Duplicate;
            }

            // A message mapped with no body yet is serialized into the row
            envelope.Serializer ??= _runtime.Options.DetermineSerializer(envelope);
            envelope.Status = EnvelopeStatus.Incoming;
            envelope.OwnerId = _runtime.Options.Durability.AssignedNodeNumber;

            await inbox.StoreIncomingAsync(envelope).ConfigureAwait(false);
            return null;
        }
        catch (DuplicateIncomingEnvelopeException)
        {
            // Another delivery of the same message holds the claim, or finished with it since the check above
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Unable to claim pushed message {EnvelopeId} ({MessageType}) received at {Address} in the inbox. Asking the broker to redeliver it",
                envelope.Id, envelope.MessageType, Address);
            return PushReceiveOutcome.Retry;
        }

        try
        {
            if (await inbox.TryFindHandledAsync(envelope, cancellation).ConfigureAwait(false) == true)
            {
                logDuplicate(envelope);
                return PushReceiveOutcome.Duplicate;
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Unable to read the inbox row of pushed message {EnvelopeId}", envelope.Id);
        }

        // Still running elsewhere. Acking this delivery would lose the message if that run then asks for a retry
        _logger.LogInformation(
            "Pushed message {EnvelopeId} ({MessageType}) received at {Address} is already being handled by another delivery. Asking the broker to redeliver it later",
            envelope.Id, envelope.MessageType, Address);
        return PushReceiveOutcome.Retry;
    }

    private void logDuplicate(Envelope envelope)
    {
        _logger.LogInformation("Skipping pushed message {EnvelopeId} ({MessageType}) received at {Address} because the inbox has already handled it",
            envelope.Id, envelope.MessageType, Address);
    }

    private async Task recordHandledAsync(Envelope envelope, bool succeeded)
    {
        var inbox = _runtime.Storage.Inbox;
        try
        {
            await inbox.MarkIncomingEnvelopeAsHandledAsync(envelope).ConfigureAwait(false);
            if (succeeded) return;

            // Moving the message to dead letter storage deleted the claim, so record it as handled as well
            var handled = Envelope.ForPersistedHandled(envelope, DateTimeOffset.UtcNow, _runtime.Options.Durability);
            await inbox.StoreIncomingAsync(handled).ConfigureAwait(false);
        }
        catch (DuplicateIncomingEnvelopeException)
        {
            // The claim was marked handled above. It is recorded either way
        }
        catch (Exception e)
        {
            // The message itself was handled and must still be acked. The claim stays Incoming and owned by this
            // node, so redeliveries get the retry status until the stale clock runs out and it is recovered
            _logger.LogError(e, "Unable to record pushed message {EnvelopeId} as handled in the inbox",
                envelope.Id);
        }
    }

    private async Task releaseClaimAsync(Envelope envelope)
    {
        try
        {
            await _runtime.Storage.Inbox.DeleteIncomingEnvelopeAsync(envelope).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Redeliveries get the retry status until the claim's stale clock runs out and it is recovered
            _logger.LogError(e, "Unable to release the inbox claim of pushed message {EnvelopeId} for the broker's redelivery",
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
                channel.Succeeded = true;
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

    // Acked because the handler succeeded, rather than because the error handling discarded or dead lettered it
    public bool Succeeded { get; set; }

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
