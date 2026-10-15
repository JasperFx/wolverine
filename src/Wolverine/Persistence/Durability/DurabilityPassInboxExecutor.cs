using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Scheduled;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

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

    public async Task<InboxExecutionOutcome> ExecuteAsync(Envelope envelope, CancellationToken token,
        bool forwardExternalDestinations = false)
    {
        // An in-memory-only reference that does not survive persistence. See GH-2318
        envelope.Store ??= _store;

        if (forwardExternalDestinations && isForExternalEndpoint(envelope))
        {
            return await forwardAsync(envelope).ConfigureAwait(false);
        }

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

    // A due scheduled send to an endpoint that schedules natively, like a database queue, is parked in the inbox
    // under that endpoint's address, unwrapped. The other modes' scheduled job hands such a row over to the
    // endpoint when there is no listener for it (WolverineRuntime.HandOverAsync). Executing it here would handle
    // it locally instead, or drop it when this application has no handler for it
    private bool isForExternalEndpoint(Envelope envelope)
    {
        if (envelope.Destination == null || envelope.Destination.Scheme == TransportConstants.Local) return false;
        if (envelope.ContentType == TransportConstants.SerializedEnvelope) return false;

        // A scheduled retry of a message this application received from that endpoint is handled here
        return _runtime.Options.Transports.TryGetEndpoint(envelope.Destination) is not { IsListener: true };
    }

    // In the order HandOverAsync uses (GH-4824): the outbox row first, so the message is never only in flight,
    // then the inbox row retired before the send, so a listener on that address never finds the id in its inbox
    // and drops the delivered message as a duplicate (GH-4316). Then sent inline, as nothing runs in the
    // background under ServerlessWithOutbox. A failed send leaves the outbox row for the outgoing recovery
    private async Task<InboxExecutionOutcome> forwardAsync(Envelope envelope)
    {
        ISendingAgent agent;
        try
        {
            agent = _runtime.Endpoints.GetOrBuildSendingAgent(envelope.Destination!);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Unable to find a sending agent for due scheduled message {EnvelopeId} to {Destination}",
                envelope.Id, envelope.Destination);
            return InboxExecutionOutcome.Failed;
        }

        envelope.ScheduledTime = null;

        bool stored;
        try
        {
            stored = await agent.TryStoreOutgoingAsync(envelope).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Unable to move due scheduled message {EnvelopeId} to the outbox for {Destination}",
                envelope.Id, envelope.Destination);
            return InboxExecutionOutcome.Failed;
        }

        try
        {
            await envelope.Store!.Inbox.DeleteIncomingEnvelopeAsync(envelope).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // The message is in the outbox either way. A stranded inbox row is recoverable by hand
            _logger.LogError(e, "Unable to delete the inbox row of due scheduled message {EnvelopeId} after moving it to the outbox",
                envelope.Id);
        }

        var sender = agent switch
        {
            SendingAgent a => a.Sender,
            InlineSendingAgent a => a.Sender,
            ISender s => s,
            _ => null
        };

        if (sender == null || agent.Latched) return InboxExecutionOutcome.Settled;

        try
        {
            await sender.SendToBrokerAsync(envelope).ConfigureAwait(false);
            _runtime.MessageTracking.Sent(envelope);
        }
        catch (Exception e)
        {
            _logger.LogError(e,
                "Unable to send due scheduled message {EnvelopeId} to {Destination}. It is left in the outbox for a later durability pass",
                envelope.Id, envelope.Destination);
            return InboxExecutionOutcome.Settled;
        }

        if (stored)
        {
            try
            {
                await envelope.Store!.Outbox.DeleteOutgoingAsync(envelope).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // Already sent, so the worst case is a duplicate send by a later pass
                _logger.LogError(e, "Unable to delete the outbox row of due scheduled message {EnvelopeId} after sending it",
                    envelope.Id);
            }
        }

        return InboxExecutionOutcome.Settled;
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
