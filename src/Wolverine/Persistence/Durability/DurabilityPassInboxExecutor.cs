using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Scheduled;
using Wolverine.Transports;

namespace Wolverine.Persistence.Durability;

internal enum InboxExecutionOutcome
{
    /// <summary>
    ///     The message was handled, or the error handling settled its row: dead lettered, discarded or
    ///     rescheduled for a retry
    /// </summary>
    Settled,

    /// <summary>
    ///     Nothing settled the row, so it is left as it was for a later pass
    /// </summary>
    Failed
}

/// <summary>
///     GH-5028. Runs a persisted inbox envelope inline, inside a durability pass. There are no local queues and
///     no running listeners under <see cref="DurabilityMode.ServerlessWithOutbox" />, so the pass is the only
///     thing that can execute a recovered or due scheduled message. The error policies apply as usual: a
///     scheduled retry goes back into the inbox for a later pass, and a dead letter moves the row to dead letter
///     storage
/// </summary>
internal class DurabilityPassInboxExecutor : IHandlerPipeline
{
    private readonly WolverineRuntime _runtime;
    private readonly IMessageStore _store;
    private readonly ILogger _logger;
    private readonly LightweightCache<Type, IExecutor> _executors;

    public DurabilityPassInboxExecutor(IWolverineRuntime runtime, IMessageStore store, ILogger logger)
    {
        _runtime = (WolverineRuntime)runtime;
        _store = store;
        _logger = logger;
        _executors = new LightweightCache<Type, IExecutor>(type => ((IExecutorFactory)_runtime).BuildFor(type));
    }

    public async Task<InboxExecutionOutcome> ExecuteAsync(Envelope envelope, CancellationToken token)
    {
        // An in-memory-only reference that does not survive persistence. See GH-2318
        envelope.Store ??= _store;

        if (envelope.Message == null && envelope.Serializer == null)
        {
            // A scheduled send to a transport that cannot schedule natively is wrapped in an envelope of its own
            envelope.Serializer = envelope.ContentType == TransportConstants.SerializedEnvelope
                ? EnvelopeReaderWriter.Instance
                : _runtime.Options.DetermineSerializer(envelope);
        }

        var channel = new DurabilityPassChannelCallback(this, envelope.Store);
        await invokeAsync(envelope, channel, token).ConfigureAwait(false);

        return channel.Settled ? InboxExecutionOutcome.Settled : InboxExecutionOutcome.Failed;
    }

    // Retries from the error policies (RetryNow, RetryWithCooldown) come back through here by way of the
    // channel's Pipeline, so they get the same inline flush as the first attempt
    Task IHandlerPipeline.InvokeAsync(Envelope envelope, IChannelCallback channel)
    {
        return invokeAsync(envelope, (DurabilityPassChannelCallback)channel, _runtime.Cancellation);
    }

    Task IHandlerPipeline.InvokeAsync(Envelope envelope, IChannelCallback channel, Activity activity)
    {
        return invokeAsync(envelope, (DurabilityPassChannelCallback)channel, _runtime.Cancellation);
    }

    ValueTask<IContinuation> IHandlerPipeline.TryDeserializeEnvelope(Envelope envelope)
    {
        return _runtime.Pipeline.TryDeserializeEnvelope(envelope);
    }

    private async Task invokeAsync(Envelope envelope, DurabilityPassChannelCallback channel,
        CancellationToken token)
    {
        var activity = WolverineTracing.StartExecuting(envelope);
        var context = _runtime.ExecutionPool.Get();
        context.ReadEnvelope(envelope, channel);

        try
        {
            var continuation = await executeAsync(context, envelope, token).ConfigureAwait(false);

            if (continuation is MessageSucceededContinuation)
            {
                // Cascading messages go out inline, before the row is marked as handled
                await context.SendAllOutgoingMessagesInlineAsync().ConfigureAwait(false);
                await context.CompleteAsync().ConfigureAwait(false);
                context.CompletionTrackerFor(_runtime).MessageSucceeded(envelope);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            else
            {
                await continuation.ExecuteAsync(context, _runtime, DateTimeOffset.UtcNow, activity)
                    .ConfigureAwait(false);

                // A failure acknowledgement or Fault<T> published by the error handling
                await context.SendAllOutgoingMessagesInlineAsync().ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e,
                "Failure while executing inbox message {EnvelopeId} ({MessageType}) at {Destination} in a durability pass. It is left in the inbox for a later pass",
                envelope.Id, envelope.MessageType, envelope.Destination);
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

    private async ValueTask<IContinuation> executeAsync(MessageContext context, Envelope envelope,
        CancellationToken token)
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
        return await executor.ExecuteAsync(context, token).ConfigureAwait(false);
    }
}

/// <summary>
///     Settles an inbox row executed by a durability pass in place of a listener's ack
/// </summary>
internal class DurabilityPassChannelCallback : IChannelCallback
{
    private readonly IMessageStore _store;
    private bool _failed;
    private bool _completed;

    public DurabilityPassChannelCallback(IHandlerPipeline pipeline, IMessageStore store)
    {
        Pipeline = pipeline;
        _store = store;
    }

    public IHandlerPipeline? Pipeline { get; }

    // Anything but a requeue or a blow up settled the row: handled, discarded, dead lettered or rescheduled
    public bool Settled => _completed || !_failed;

    public async ValueTask CompleteAsync(Envelope envelope)
    {
        await _store.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope).ConfigureAwait(false);
        _completed = true;
    }

    // Requeue has no queue to go to, so the row stays for a later pass
    public ValueTask DeferAsync(Envelope envelope)
    {
        _failed = true;
        return ValueTask.CompletedTask;
    }

    public void Fail()
    {
        if (!_completed) _failed = true;
    }
}
