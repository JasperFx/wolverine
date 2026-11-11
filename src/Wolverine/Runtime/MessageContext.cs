using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Logging;
using Wolverine.Runtime.WorkerQueues;
using JasperFx;
using JasperFx.MultiTenancy;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Batching;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util;

namespace Wolverine.Runtime;

public enum MultiFlushMode
{
    /// <summary>
    /// The default mode, additional calls to FlushOutgoingMessages() are ignored
    /// </summary>
    OnlyOnce,

    /// <summary>
    /// Allow for multiple calls to FlushOutgoingMessages()
    /// </summary>
    AllowMultiples,

    /// <summary>
    /// Throw an exception on additional calls to FlushOutgoingMessages(). Use this to troubleshoot
    /// erroneous behavior
    /// </summary>
    AssertOnMultiples
}

public class MessageContext : MessageBus, IMessageContext, IHasTenantId, IEnvelopeTransaction, IEnvelopeLifecycle
{
    private IChannelCallback? _channel;

    private bool _hasFlushed;
    private object? _sagaId;

    // F13: this execution already settled its envelope by rescheduling it, so the success path must not then
    // complete it. See CompleteAsync
    private bool _rescheduled;

    // R2/E1: the eager idempotency check put a Handled row into the handler's own transaction and stamped the
    // envelope as if it were already persisted. Both stand only if that transaction commits, so a failed attempt
    // puts the envelope back the way the check found it. See ClearAllAsync
    private bool _eagerIdempotencyStamped;
    private EnvelopeStatus _statusBeforeEagerIdempotency;

    /// <summary>
    /// Has this context already flushed its outgoing messages? When true, any
    /// envelope enqueued afterward will be dropped by the default
    /// MultiFlushMode.OnlyOnce guard in FlushOutgoingMessagesAsync(). Cascading
    /// wrappers that must deliver regardless (e.g. SignalR responses, GH-3499)
    /// check this to reroute through a fresh context
    /// </summary>
    internal bool HasFlushed => _hasFlushed;

    /// <summary>
    ///     The metrics tracker the executing handler resolved for this envelope — stamped by the
    ///     executor at execution time so completion continuations (success, no-handler, error-queue,
    ///     discard) can honor the metrics-silent selection for system traffic instead of reporting
    ///     through the runtime's global tracker. Null outside an execution. CritterWatch GH-907 /
    ///     wolverine#3774.
    /// </summary>
    internal IMessageTracker? Tracker { get; set; }

    public MessageContext(IWolverineRuntime runtime) : base(runtime)
    {
    }

    // Used implicitly in codegen
    public MessageContext(IWolverineRuntime runtime, string tenantId) : base(runtime)
    {
        TenantId = runtime.Options.Durability.TenantIdStyle.MaybeCorrectTenantId(tenantId);
    }

    Task<bool> IEnvelopeTransaction.TryMakeEagerIdempotencyCheckAsync(Envelope envelope,
        DurabilitySettings settings, CancellationToken cancellation)
    {
        return Task.FromResult(true);
    }

    /// <summary>
    /// Governs how the MessageContext will handle subsequent calls to FlushOutgoingMessages(). The
    /// default behavior is to quietly ignore any additional calls
    /// </summary>
    public MultiFlushMode MultiFlushMode { get; set; } = MultiFlushMode.OnlyOnce;

    internal List<Envelope> Scheduled { get; } = new();

    private bool hasRequestedReply()
    {
        return Envelope != null && Envelope.ReplyRequested.IsNotEmpty();
    }

    private bool isMissingRequestedReply()
    {
        var replyRequested = Envelope!.ReplyRequested;
        foreach (var envelope in Outstanding)
        {
            if (envelope.MessageType == replyRequested) return false;
        }

        if (_sent != null)
        {
            foreach (var envelope in _sent)
            {
                if (envelope.MessageType == replyRequested) return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Potentially throws an exception if the current message has already been processed
    /// </summary>
    /// <param name="cancellation"></param>
    /// <exception cref="DuplicateIncomingEnvelopeException"></exception>
    public async Task AssertEagerIdempotencyAsync(CancellationToken cancellation)
    {
        if (Envelope == null || Envelope.WasPersistedInInbox ) return;
        if (Transaction == null || Transaction is MessageContext)
        {
            var exists = await Runtime.Storage.Inbox.ExistsAsync(Envelope, cancellation).ConfigureAwait(false);
            if (exists)
            {
                throw new DuplicateIncomingEnvelopeException(Envelope);
            }

            return;
        }

        // R2/E1: every store puts the Handled row into the handler's own transaction, so it commits or rolls back
        // with the work it records. Remember what the envelope looked like first, so a rollback can undo the stamp
        var statusBefore = Envelope.Status;
        var check = await Transaction.TryMakeEagerIdempotencyCheckAsync(Envelope, Runtime.Options.Durability, cancellation).ConfigureAwait(false);
        if (!check)
        {
            throw new DuplicateIncomingEnvelopeException(Envelope);
        }

        Envelope.WasPersistedInInbox = true;
        _statusBeforeEagerIdempotency = statusBefore;
        _eagerIdempotencyStamped = true;
    }

    /// <summary>
    ///     R2/E1. The attempt failed, so the handler's transaction -- and the Handled row the eager idempotency check
    ///     put into it -- rolled back. Left stamped, the envelope skipped the check on its retry and its commit
    ///     marked a row that no longer existed, so no record of completion survived and a later redelivery ran the
    ///     message again
    /// </summary>
    private void undoEagerIdempotencyStamp()
    {
        if (!_eagerIdempotencyStamped) return;
        _eagerIdempotencyStamped = false;

        if (Envelope == null) return;
        Envelope.WasPersistedInInbox = false;
        Envelope.Status = _statusBeforeEagerIdempotency;
    }

    public async Task PersistHandledAsync()
    {
        var handled = Envelope.ForPersistedHandled(Envelope!, DateTimeOffset.UtcNow, Runtime.Options.Durability);
        try
        {
            await Runtime.Storage.Inbox.StoreIncomingAsync(handled).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Runtime.Logger.LogError(e, "Error trying to mark message {Id} as handled. Retrying later.", handled.Id);

            // Retry this off to the side...
            await new MessageBus(Runtime).PublishAsync(new PersistHandled(handled)).ConfigureAwait(false);
        }
    }

    public Task FlushOutgoingMessagesAsync()
    {
        // GH-5016: nothing can be trusted to run after a serverless request completes, so every flush sends
        // inline -- including the plain FlushOutgoingMessages postprocessor of a handler or HTTP endpoint that
        // has no transactional middleware to do it on commit
        if (Runtime.Options.Durability.Mode == DurabilityMode.ServerlessWithOutbox)
        {
            return SendAllOutgoingMessagesInlineAsync();
        }

        return flushOutgoingMessagesAsync();
    }

    private async Task flushOutgoingMessagesAsync()
    {
        var outgoing = await beginFlushAsync().ConfigureAwait(false);
        if (outgoing == null) return;

        foreach (var envelope in outgoing)
        {
            // https://github.com/JasperFx/wolverine/issues/2006
            if (envelope == null) continue;

            await flushEnvelopeAsync(envelope).ConfigureAwait(false);
        }

        await completeFlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Send every outgoing message in this context <b>right now</b>, awaiting the broker, instead of
    ///     handing them off to the sending agents in the background. Use this after the transaction
    ///     commits wherever nothing can be trusted to run after the request completes, such as a
    ///     serverless function under <see cref="DurabilityMode.ServerlessWithOutbox" />.
    /// </summary>
    /// <remarks>
    ///     <para>Every envelope bound for an external destination is sent through its endpoint's transport
    ///     sender directly, whether that endpoint is configured as inline, buffered or durable. A batching
    ///     sender is bypassed for its transport protocol, so a send only counts once the broker has accepted
    ///     the message (GH-5034). The envelopes for one destination go out in batches of its endpoint's
    ///     MessageBatchSize, one broker round trip per batch, and different destinations are sent
    ///     concurrently.</para>
    ///     <para>The outbox rows of the envelopes that were sent are deleted in <b>one</b> batched call per
    ///     message store rather than one round trip per envelope. An envelope whose send fails is left in
    ///     the outbox for the next durability pass to recover, and logged. A durable envelope that had no
    ///     outbox row yet, because there is no external transaction, is stored to the outbox when its send
    ///     fails. A durable envelope the transport reports as a serialization failure, which no retry can fix, is
    ///     moved to dead letter storage instead and its outbox row deleted (GH-5047).</para>
    ///     <para>Messages scheduled for later, and destinations without a transport sender (local queues),
    ///     are flushed exactly as <see cref="FlushOutgoingMessagesAsync" /> flushes them.</para>
    /// </remarks>
    public async Task SendAllOutgoingMessagesInlineAsync()
    {
        var outgoing = await beginFlushAsync().ConfigureAwait(false);
        if (outgoing == null) return;

        // With an external transaction (Marten, EF Core, the RDBMS stores) the durable envelopes were
        // persisted with it. With this context as its own transaction nothing has been written yet
        var alreadyPersisted = Transaction != null && !ReferenceEquals(Transaction, this);
        List<Envelope>? toDelete = null;
        List<(Envelope Envelope, ISendingAgent Agent, ISender Sender)>? sendable = null;

        foreach (var envelope in outgoing)
        {
            // https://github.com/JasperFx/wolverine/issues/2006
            if (envelope == null) continue;

            var agent = envelope.Sender;
            var sender = agent switch
            {
                SendingAgent a => a.Sender,
                InlineSendingAgent a => a.Sender,

                // An agent that is its own transport sender, like the stub transport's endpoints
                ISender s => s,
                _ => null
            };

            if (agent == null || sender == null || agent.Latched || envelope.IsScheduledForLater(DateTimeOffset.UtcNow))
            {
                await flushEnvelopeAsync(envelope).ConfigureAwait(false);
                continue;
            }

            envelope.Status = EnvelopeStatus.Outgoing;
            envelope.OwnerId = Runtime.Options.Durability.OutgoingOwnerId;
            envelope.ReplyUri ??= agent.ReplyUri;

            (sendable ??= new()).Add((envelope, agent, sender));
        }

        if (sendable != null)
        {
            // One group per transport sender, each sent in as few broker round trips as its transport allows.
            // The groups go to different destinations, so they are sent concurrently, while the envelopes of one
            // group keep their order
            var groups = sendable.GroupBy(x => x.Sender).ToArray();
            var results = await Task.WhenAll(groups.Select(group =>
                group.Key.SendToBrokerAsync(group.Select(x => x.Envelope).ToArray()))).ConfigureAwait(false);

            for (var i = 0; i < groups.Length; i++)
            {
                foreach (var (envelope, agent, _) in groups[i])
                {
                    if (results[i].WasAccepted(envelope))
                    {
                        Runtime.MessageTracking.Sent(envelope);

                        if (alreadyPersisted && agent.IsDurable)
                        {
                            (toDelete ??= new()).Add(envelope);
                        }

                        continue;
                    }

                    if (agent.IsDurable && results[i].IsSerializationFailure(envelope, out var serializationFailure))
                    {
                        // GH-5047. The transport can never take it, so a later pass sending it again cannot help.
                        // Its outbox row goes with the sent ones below, once the dead letter is written
                        if (await UnsendableOutgoingEnvelopes.TryMoveToDeadLetterStorageAsync(Runtime,
                                envelope.Store ?? Runtime.Storage, envelope, serializationFailure, Runtime.Logger)
                                .ConfigureAwait(false))
                        {
                            if (alreadyPersisted)
                            {
                                (toDelete ??= new()).Add(envelope);
                            }

                            continue;
                        }
                    }

                    if (!agent.IsDurable)
                    {
                        Runtime.Logger.LogError(results[i].Exception,
                            "Unable to send outgoing message {Envelope} inline to non-durable destination {Destination}, so it has been discarded",
                            envelope, envelope.Destination);
                        Runtime.MessageTracking.DiscardedEnvelope(envelope);
                        continue;
                    }

                    // GH-5038. Outside ServerlessWithOutbox the outbox row is owned by this live node, and nothing
                    // recovers a live node's rows: the durability agent only takes owner 0, and the dead-node sweep
                    // waits for the node to die. Hand the envelope to the destination's own sending agent instead,
                    // which keeps retrying it exactly as a normal flush would have
                    if (Runtime.Options.Durability.Mode != DurabilityMode.ServerlessWithOutbox)
                    {
                        Runtime.Logger.LogError(results[i].Exception,
                            "Unable to send outgoing message {Envelope} inline to {Destination}. Handing it to the destination's sending agent to retry",
                            envelope, envelope.Destination);

                        await flushEnvelopeAsync(envelope).ConfigureAwait(false);
                        continue;
                    }

                    Runtime.Logger.LogError(results[i].Exception,
                        "Unable to send outgoing message {Envelope} inline to {Destination}. It is left in the outbox to be recovered by a later durability pass",
                        envelope, envelope.Destination);

                    if (!alreadyPersisted)
                    {
                        await storeUnsentEnvelopeAsync(envelope).ConfigureAwait(false);
                    }
                }
            }
        }

        if (toDelete != null)
        {
            await deleteSentOutboxRowsAsync(toDelete).ConfigureAwait(false);
        }

        await completeFlushAsync().ConfigureAwait(false);
    }

    private async Task storeUnsentEnvelopeAsync(Envelope envelope)
    {
        try
        {
            await envelope.SerializeAheadAsync().ConfigureAwait(false);
            await (envelope.Store ?? Runtime.Storage).Outbox
                .StoreOutgoingAsync(envelope, Runtime.Options.Durability.OutgoingOwnerId).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Runtime.Logger.LogError(e,
                "Unable to store unsent outgoing message {Envelope} in the outbox, so it has been discarded",
                envelope);
            Runtime.MessageTracking.DiscardedEnvelope(envelope);
        }
    }

    // One DeleteOutgoingAsync(Envelope[]) per message store, not one round trip per envelope
    private async Task deleteSentOutboxRowsAsync(List<Envelope> sent)
    {
        foreach (var byStore in sent.GroupBy(x => x.Store ?? Runtime.Storage))
        {
            try
            {
                await byStore.Key.Outbox.DeleteOutgoingAsync(byStore.ToArray()).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // The messages were sent, so the worst case is a duplicate send by a later durability pass
                Runtime.Logger.LogError(e,
                    "Unable to delete the outbox rows of {Count} message(s) that were already sent inline. A later durability pass may send them again",
                    byStore.Count());
            }
        }
    }

    // Answers null when there is nothing to flush
    private async Task<Envelope[]?> beginFlushAsync()
    {
        if (_hasFlushed)
        {
            switch (MultiFlushMode)
            {
                case MultiFlushMode.OnlyOnce:
                    lock (_outstandingLock)
                    {
                        if (_outstanding.Count > 0)
                        {
                            // GH-3499 -- a silent drop of enqueued envelopes is the worst outcome,
                            // so at least make the loss visible
                            Runtime.Logger.LogWarning(
                                "MessageContext for {MessageType} has already flushed its outgoing messages, so {Count} envelope(s) enqueued after that flush will not be sent. This can happen when messages are enqueued after an explicit transaction commit (e.g. SaveChangesAsync()) has already drained the context. Consider MultiFlushMode.AllowMultiples if multiple flushes are expected",
                                Envelope?.MessageType, _outstanding.Count);
                        }
                    }

                    return null;

                case MultiFlushMode.AllowMultiples:
                    Runtime.Logger.LogDebug("Received multiple calls to FlushOutgoingMessagesAsync() to a single MessageContext");
                    break;

                case MultiFlushMode.AssertOnMultiples:
                    throw new InvalidOperationException(
                        $"This MessageContext does not allow multiple calls to {nameof(FlushOutgoingMessagesAsync)} because {nameof(MultiFlushMode)} = {MultiFlushMode}");
            }
        }

        // GH-4322: the assertion no-ops unless a reply was requested, but awaiting the async
        // method still built a state machine per message — the overwhelmingly common
        // fire-and-forget case now skips it outright.
        if (hasRequestedReply())
        {
            await AssertAnyRequiredResponseWasGenerated().ConfigureAwait(false);
        }

        // Snapshot under lock so concurrent publishes from a Marten projection
        // (Block parallelism = 10 in AggregationRunner) cannot corrupt the list
        // while we're iterating it. GH-2529.
        lock (_outstandingLock)
        {
            if (_outstanding.Count == 0) return null;
            return _outstanding.ToArray();
        }
    }

    private async Task flushEnvelopeAsync(Envelope envelope)
    {
        try
        {
            var utcNow = DateTimeOffset.UtcNow;
            if (envelope.IsScheduledForLater(utcNow))
            {
                if (!envelope.Sender!.IsDurable)
                {
                    if (envelope.Sender!.SupportsNativeScheduledSendFor(envelope, utcNow))
                    {
                        Runtime.Logger.LogDebug("Sending scheduled envelope {EnvelopeId} ({MessageType}) via native scheduled send to {Destination}", envelope.Id, envelope.MessageType, envelope.Destination);
                        await sendEnvelopeAsync(envelope).ConfigureAwait(false);
                    }
                    else
                    {
                        // Non-durable sender, no native scheduling. In current Wolverine this
                        // branch is effectively unreachable for non-local transports — the
                        // routing layer (MessageRoute.WriteEnvelope) already swaps such
                        // envelopes to the local://durable queue, which means Sender.IsDurable
                        // would be true above. Kept for defense in depth.
                        Runtime.Logger.LogDebug("Scheduling envelope {EnvelopeId} ({MessageType}) for in-memory execution (non-durable, no native scheduling) to {Destination}", envelope.Id, envelope.MessageType, envelope.Destination);
                        Runtime.ScheduleLocalExecutionInMemory(envelope.ScheduledTime!.Value, envelope);
                    }
                }
                else
                {
                    Runtime.Logger.LogDebug("Envelope {EnvelopeId} ({MessageType}) is scheduled with durable sender to {Destination}, relying on durable inbox scheduling", envelope.Id, envelope.MessageType, envelope.Destination);
                }

                // If NullMessageStore, then we're calling a different Send method that is marking the message
                if (Runtime.Storage is not NullMessageStore)
                {
                    // See https://github.com/JasperFx/wolverine/issues/1697
                    Runtime.MessageTracking.Sent(envelope);
                }
            }
            else
            {
                await sendEnvelopeAsync(envelope).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            // GH-4662. This used to read "most likely due to serialization issues", which was a fair
            // guess back when a durable store failure was swallowed by a RetryBlock and could never
            // reach this catch. It can now, and an unreachable message store is the more common cause
            // of the two, so name both -- and name the envelope, which this never did.
            Runtime.Logger.LogError(e,
                "Unable to send outgoing message {Envelope}, so it has been discarded. This is usually either a serialization failure or an unreachable message store",
                envelope);
            Runtime.MessageTracking.DiscardedEnvelope(envelope);
        }

        async Task sendEnvelopeAsync(Envelope envelope)
        {
            if (ReferenceEquals(this, Transaction))
            {
                await envelope.StoreAndForwardAsync().ConfigureAwait(false);
            }
            else
            {
                await envelope.QuickSendAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task completeFlushAsync()
    {
        if (Scheduled.Count > 0 && ReferenceEquals(Transaction, this))
        {
            await flushScheduledMessagesAsync().ConfigureAwait(false);
        }

        _sent ??= new();
        lock (_outstandingLock)
        {
            _sent.AddRange(_outstanding);
            _outstanding.Clear();
        }

        _hasFlushed = true;
    }

    private List<Envelope>? _sent;

    public async Task AssertAnyRequiredResponseWasGenerated()
    {
        if (hasRequestedReply() && _channel is not InvocationCallback)
        {
            if (isMissingRequestedReply())
            {
                var failureDescription = $"No response was created for expected response '{Envelope!.ReplyRequested}' back to reply-uri {Envelope.ReplyUri}. ";

                Envelope[] outstandingSnapshot;
                lock (_outstandingLock)
                {
                    outstandingSnapshot = _outstanding.ToArray();
                }

                if (outstandingSnapshot.Length > 0)
                {
                    var types = new List<string>(outstandingSnapshot.Length + (_sent?.Count ?? 0));
                    foreach (var e in outstandingSnapshot) types.Add(e.MessageType!);
                    if (_sent != null)
                    {
                        foreach (var e in _sent) types.Add(e.MessageType!);
                    }

                    failureDescription += "Actual cascading messages were " + string.Join(", ", types);
                }
                else
                {
                    failureDescription += $"No cascading messages were created by this handler for the expected response type {Envelope.ReplyRequested}";
                }

                await SendFailureAcknowledgementAsync(failureDescription).ConfigureAwait(false);
            }
            else
            {
                Activity.Current?.SetTag("reply-uri", Envelope!.ReplyUri!.ToString());
                // Debug, not Information: this fires on every request/reply response, and every
                // sibling per-message log in this class is Debug-level.
                Runtime.Logger.LogDebug("Sending requested reply of type {MessageType} to reply-uri {ReplyUri}", Envelope!.ReplyRequested, Envelope.ReplyUri);
            }
        }
    }

    public async ValueTask CompleteAsync()
    {
        if (_channel == null || Envelope == null)
        {
            throw new InvalidOperationException("No Envelope is active for this context");
        }

        // HasBeenAcked means the BROKER delivery is settled. For a durable endpoint that already happened
        // before the handler ran (GH-4026), and SQS records it on the flag (GH-4489) -- but the durable
        // receiver's completion is the inbox row, not the broker, so it must still run. GH-5119.
        if (Envelope.HasBeenAcked && _channel is not DurableReceiver) return;

        // F13: a handler that called ReScheduleAsync and then returned normally already handed its inbox row back
        // as Scheduled. Completing it now marked that row Handled -- the mark-as-handled UPDATE matches on the
        // identity alone -- and the rescheduled run was lost. The reschedule IS this execution's settlement
        if (_rescheduled && _channel is DurableReceiver) return;

        await _channel.CompleteAsync(Envelope).ConfigureAwait(false);
        Envelope.HasBeenAcked = true;
    }

    public async ValueTask DeferAsync()
    {
        if (_channel == null || Envelope == null)
        {
            throw new InvalidOperationException("No Envelope is active for this context");
        }

        Runtime.MessageTracking.Requeued(Envelope);
        await _channel.DeferAsync(Envelope).ConfigureAwait(false);
    }

    public async Task ReScheduleAsync(DateTimeOffset scheduledTime)
    {
        if (_channel == null || Envelope == null)
        {
            throw new InvalidOperationException("No Envelope is active for this context");
        }

        Runtime.MessageTracking.Requeued(Envelope);
        Envelope.ScheduledTime = scheduledTime;
        if (tryGetRescheduler(_channel, Envelope) is ISupportNativeScheduling c)
        {
            Runtime.Logger.LogDebug("Rescheduling envelope {EnvelopeId} ({MessageType}) via native scheduling to {ScheduledTime}", Envelope.Id, Envelope.MessageType, scheduledTime);
            await c.MoveToScheduledUntilAsync(Envelope, Envelope.ScheduledTime.Value).ConfigureAwait(false);
        }
        else
        {
            Runtime.Logger.LogDebug("Rescheduling envelope {EnvelopeId} ({MessageType}) via durable inbox to {ScheduledTime}", Envelope.Id, Envelope.MessageType, scheduledTime);
            await Storage.Inbox.RescheduleExistingEnvelopeForRetryAsync(Envelope).ConfigureAwait(false);
        }

        _rescheduled = true;
    }

    /// <summary>
    ///     F13. Has this execution settled its envelope by rescheduling it? Transactional middleware must not then
    ///     mark the same inbox row Handled inside the handler's transaction
    /// </summary>
    internal bool HasRescheduledEnvelope => _rescheduled;

    private ISupportNativeScheduling? tryGetRescheduler(IChannelCallback? channel, Envelope e)
    {
        // TODO: is that ok, or should we modify Task ISupportNativeScheduling.MoveToScheduledUntilAsync(Envelope envelope, DateTimeOffset time) in DurableReceiver and BufferedReceiver?
        // Gate on NativeSchedulingEnabled so a listener whose native scheduling is conditional (e.g.
        // Pulsar without a retry-letter topic) is skipped and the durable/buffered channel below is
        // used instead of silently no-op'ing. Mirrors the NativeDeadLetterQueueEnabled gating below.
        if (e.Listener is ISupportNativeScheduling { NativeSchedulingEnabled: true } c2)
        {
            return c2;
        }

        if (channel is ISupportNativeScheduling { NativeSchedulingEnabled: true } c)
        {
            return c;
        }

        return default;
    }
    private ISupportDeadLetterQueue? tryGetDeadLetterQueue(IChannelCallback? channel, Envelope e)
    {
        if (_channel is ISupportDeadLetterQueue { NativeDeadLetterQueueEnabled: true } c)
        {
            return c;
        }

        if (e.Listener is ISupportDeadLetterQueue { NativeDeadLetterQueueEnabled: true } c2)
        {
            return c2;
        }

        return default;
    }

    public async Task MoveToDeadLetterQueueAsync(Exception exception)
    {
        // Don't bother with agent commands
        if (Envelope?.Message is IAgentCommand) return;

        if (_channel == null || Envelope == null)
        {
            throw new InvalidOperationException("No Envelope is active for this context");
        }

        if (Envelope.Batch != null)
        {
            await moveBatchToDeadLetterQueueAsync(Envelope, exception).ConfigureAwait(false);
            return;
        }

        var deadLetterQueue = tryGetDeadLetterQueue(_channel, Envelope);
        if (deadLetterQueue is not null)
        {
            await deadLetterQueue.MoveToErrorsAsync(Envelope, exception).ConfigureAwait(false);
            return;
        }

        // If persistable, persist
        var intercepted = await interceptDeadLetterAsync(Envelope, exception).ConfigureAwait(false);
        await Storage.Inbox.MoveToDeadLetterStorageAsync(Envelope, intercepted).ConfigureAwait(false);

        // If this is Inline
        await _channel.CompleteAsync(Envelope).ConfigureAwait(false);
    }

    /// <summary>
    ///     Dead letter every member of a batch, each under its own identity
    /// </summary>
    private async Task moveBatchToDeadLetterQueueAsync(Envelope batch, Exception exception)
    {
        // GH-5105. A member that cannot be moved must not stop the rest: stopping at the first left every later
        // member unmoved and, for a durable receiver, Incoming and owned by this node
        var handedBack = await MoveBatchMembersToDeadLetterQueueAsync(batch.Batch!, exception).ConfigureAwait(false);

        var pendingCounts = (Runtime as WolverineRuntime)?.BatchingPendingCounts;

        if (handedBack.Count == 0)
        {
            if (tryGetDeadLetterQueue(_channel, batch) is not null)
            {
                // F8. The caller completes the batch after a native move, but release the members' pending counts
                // here all the same, whoever the caller is: a batch that never reaches its completion would
                // otherwise hold its listener's QueueCount, which back-pressure reads, up for good
                pendingCounts?.SettleBatch(batch);
            }
            else
            {
                await _channel!.CompleteAsync(batch).ConfigureAwait(false);

                // The batch is settled; the caller's own completion must not settle its members a second time
                batch.HasBeenAcked = true;
            }

            return;
        }

        // F2 / F8. The members that could not be moved were handed back to be executed again on their own. Take them
        // out of the batch without settling their inbox rows, and settle everything else now: the caller does not
        // complete an envelope whose dead-letter move failed, which used to leak every member's pending count
        BatchReplay.HandBack(Runtime, batch, handedBack);
        await _channel!.CompleteAsync(batch).ConfigureAwait(false);
        batch.HasBeenAcked = true;

        throw new DeadLetterMoveFailedException(
            $"{handedBack.Count} of the members of batch {batch.Id} could not be moved to dead letter storage; they were handed back to be executed again",
            null);
    }

    /// <summary>
    ///     Dead-letter a specific SUBSET of a batch's member envelopes without touching the other members
    ///     or completing the batch envelope. Used by the batch item-isolation continuations to move only the
    ///     poison items to the DLQ while the survivors are acked or replayed. The batch envelope itself is
    ///     settled afterwards by the caller's CompleteAsync(). GH-3289.
    /// </summary>
    /// <returns>
    ///     F2. The members that could NOT be dead lettered and were handed back to be executed again on their own
    ///     instead. The caller must take them out of the batch (<see cref="BatchReplay.Detach" />) before it
    ///     completes the batch, or the completion would settle, and so lose, them
    /// </returns>
    internal async Task<IReadOnlyList<Envelope>> MoveBatchMembersToDeadLetterQueueAsync(IReadOnlyList<Envelope> members,
        Exception exception)
    {
        if (_channel == null || Envelope == null)
        {
            throw new InvalidOperationException("No Envelope is active for this context");
        }

        List<Envelope>? handedBack = null;

        var deadLetterQueue = tryGetDeadLetterQueue(_channel, Envelope);
        foreach (var member in members)
        {
            if (deadLetterQueue is not null)
            {
                try
                {
                    await deadLetterQueue.MoveToErrorsAsync(member, exception).ConfigureAwait(false);
                }
                catch (DeadLetterMoveFailedException)
                {
                    // The durable receiver already handed the member's row back and logged it (GH-5105)
                    (handedBack ??= []).Add(member);
                }
            }
            else if (!await tryMoveBatchMemberToDeadLetterStorageAsync(member, exception).ConfigureAwait(false))
            {
                (handedBack ??= []).Add(member);
            }
        }

        return handedBack ?? (IReadOnlyList<Envelope>)Array.Empty<Envelope>();
    }

    /// <returns>False if the member could not be dead lettered and was handed back to be executed again</returns>
    private async Task<bool> tryMoveBatchMemberToDeadLetterStorageAsync(Envelope member, Exception exception)
    {
        var intercepted = await interceptDeadLetterAsync(member, exception).ConfigureAwait(false);

        // Only a batch on a buffered local queue gets here: a durable one is its own dead letter queue
        if (_channel is not BufferedReceiver buffered)
        {
            await Storage.Inbox.MoveToDeadLetterStorageAsync(member, intercepted).ConfigureAwait(false);
            return true;
        }

        var failure = await buffered.BatchMembers
            .TryMoveToDeadLetterStorageAsync(member, intercepted, Runtime.Cancellation).ConfigureAwait(false);
        if (failure == null) return true;

        // B2. A member that arrived on a durable listener still has its inbox row. Hand it back to be executed again
        // later rather than settle it with the batch, which used to leave it Incoming on this live node for good
        if (BatchMemberInbox.HasUnsettledRow(member))
        {
            await buffered.BatchMembers.HandBackAsync(member).ConfigureAwait(false);

            Runtime.Logger.LogError(failure,
                "Could not move batch member {EnvelopeId} ({MessageType}, tenant {TenantId}) to dead letter storage. It was NOT dead lettered, and will be executed again after {Delay}",
                member.Id, member.MessageType, member.TenantId, buffered.BatchMembers.DeadLetterRedeliveryDelay);

            return false;
        }

        // Nothing durable holds this member any more -- it arrived on a buffered listener, or was sent locally -- so
        // there is nothing to hand back
        Runtime.Logger.LogError(failure,
            "Could not move batch member {EnvelopeId} ({MessageType}, tenant {TenantId}) to dead letter storage, and it has no inbox row to be executed again from",
            member.Id, member.MessageType, member.TenantId);

        return true;
    }

    // Give registered IDeadLetterInterceptor implementations a chance to mutate the envelope
    // (e.g. redact/encrypt the body) and/or replace the exception that gets persisted, before a
    // failed envelope is written to durable dead-letter storage. Interceptors run in registration
    // order, each receiving the exception returned by the previous one. No-op when none are registered.
    private async ValueTask<Exception?> interceptDeadLetterAsync(Envelope envelope, Exception? exception)
    {
        foreach (var interceptor in Runtime.Services.GetServices<IDeadLetterInterceptor>())
        {
            exception = await interceptor.BeforeStoreAsync(envelope, exception, Runtime.Cancellation)
                .ConfigureAwait(false);
        }

        return exception;
    }

    public Task RetryExecutionNowAsync()
    {
        if (_channel == null || Envelope == null)
        {
            throw new InvalidOperationException("No Envelope is active for this context");
        }

        return (_channel.Pipeline ?? Runtime.Pipeline).InvokeAsync(Envelope, _channel!);
    }

    public async ValueTask SendAcknowledgementAsync()
    {
        if (Envelope!.ReplyUri == null)
        {
            return;
        }

        var acknowledgement = new Acknowledgement
        {
            RequestId = Envelope.Id
        };

        var envelope = Runtime.RoutingFor(typeof(Acknowledgement))
            .RouteToDestination(acknowledgement, Envelope.ReplyUri, null);

        TrackEnvelopeCorrelation(envelope, Activity.Current);
        envelope.SagaId = Envelope.SagaId;

        try
        {
            await envelope.StoreAndForwardAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // GH-4662. This used to say it should never happen because the sending agents caught
            // everything. They deliberately do not any more -- a durable store failure propagates -- so
            // this catch is now load bearing. Swallow it on purpose: a lost acknowledgement is not worth
            // failing the message that was handled successfully.
            Runtime.Logger.LogError(e, "Failure while sending an acknowledgement for envelope {Id}", envelope.Id);
        }
    }

    public async ValueTask SendFailureAcknowledgementAsync(string failureDescription)
    {
        if (Envelope!.ReplyUri == null || Envelope.HasSentFailureAcknowledgement)
        {
            return;
        }

        // Latched before the send, so a send that fails is not tried again either
        Envelope.HasSentFailureAcknowledgement = true;

        var acknowledgement = new FailureAcknowledgement
        {
            RequestId = Envelope.Id,
            Message = failureDescription
        };

        var envelope = Runtime.RoutingFor(typeof(FailureAcknowledgement))
            .RouteToDestination(acknowledgement, Envelope.ReplyUri, null);

        TrackEnvelopeCorrelation(envelope, Activity.Current);
        envelope.SagaId = Envelope.SagaId;

        try
        {
            await envelope.StoreAndForwardAsync().ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            // I don't like this, but if this happens, then it should never have been routed to the failure ack
        }
        catch (Exception e)
        {
            // GH-4662. Same as the success acknowledgement above: this is reachable now that a durable
            // store failure propagates instead of being swallowed, and it is swallowed here on purpose.
            Runtime.Logger.LogError(e, "Failure while sending a failure acknowledgement for envelope {Id}",
                envelope.Id);
        }
    }

    Task IEnvelopeTransaction.PersistOutgoingAsync(Envelope envelope)
    {
        lock (_outstandingLock)
        {
            _outstanding.Fill(envelope);
        }
        return Task.CompletedTask;
    }

    Task IEnvelopeTransaction.PersistOutgoingAsync(Envelope[] envelopes)
    {
        lock (_outstandingLock)
        {
            _outstanding.Fill(envelopes);
        }
        return Task.CompletedTask;
    }

    Task IEnvelopeTransaction.PersistIncomingAsync(Envelope envelope)
    {
        if (envelope.Status == EnvelopeStatus.Scheduled)
        {
            Scheduled.Fill(envelope);
        }

        return Task.CompletedTask;
    }

    public ValueTask RollbackAsync()
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     Send a response message back to the original sender of the message being handled.
    ///     This can only be used from within a message handler
    /// </summary>
    /// <param name="response"></param>
    /// <param name="context"></param>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    /// <returns></returns>
    public override ValueTask RespondToSenderAsync(object response)
    {
        if (Envelope == null)
        {
            throw new InvalidOperationException(
                "This operation can only be performed while in the middle of handling an incoming message");
        }

        if (Envelope.ReplyUri == null)
        {
            throw new ArgumentOutOfRangeException(nameof(Envelope), $"There is no {nameof(Envelope.ReplyUri)}");
        }

        return EndpointFor(Envelope.ReplyUri).SendAsync(response);
    }

    /// <summary>
    /// Marks the existing envelope for rescheduling
    /// </summary>
    /// <param name="rescheduledAt"></param>
    /// <returns></returns>
    public override async Task ReScheduleCurrentAsync(DateTimeOffset rescheduledAt)
    {
        await ReScheduleAsync(rescheduledAt).ConfigureAwait(false);
    }

    internal async Task CopyToAsync(IEnvelopeTransaction other)
    {
        Envelope[] snapshot;
        lock (_outstandingLock)
        {
            snapshot = _outstanding.ToArray();
        }
        await other.PersistOutgoingAsync(snapshot).ConfigureAwait(false);

        foreach (var envelope in Scheduled) await other.PersistIncomingAsync(envelope).ConfigureAwait(false);
    }

    /// <summary>
    ///     Discard all outstanding, cascaded messages and clear the transaction
    /// </summary>
    public async ValueTask ClearAllAsync()
    {
        Scheduled.Clear();
        lock (_outstandingLock)
        {
            _outstanding.Clear();
        }

        if (Transaction != null)
        {
            try
            {
                await Transaction.RollbackAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Swallowing these exceptions as they can do nothing but harm
            }
        }

        undoEagerIdempotencyStamp();

        Transaction = null;
    }

    /// <summary>
    ///     Discard everything the failed attempt of an inline invocation queued or enlisted, and put the
    ///     context back in the state <see cref="ReadEnvelope"/> left it in, so the next attempt starts clean
    /// </summary>
    internal async ValueTask ResetForInlineRetryAsync()
    {
        await ClearAllAsync().ConfigureAwait(false);

        // GH-4743. Handlers flush DURING HandleAsync, not only at the end of the invocation -- a Marten
        // commit does it through FlushOutgoingMessagesOnCommit.AfterCommitAsync, and generated handlers
        // call it outright. So an attempt that commits and THEN fails leaves this set, and the final
        // flush in Executor.InvokeInlineAsync would hit the MultiFlushMode.OnlyOnce guard and silently
        // drop everything the SUCCESSFUL retry published -- the mirror image of the leak this method
        // exists to stop. ClearState, which is the baseline the queued path gets from a pooled context,
        // clears it for the same reason.
        _hasFlushed = false;

        // ClearAllAsync nulls the transaction, but an inline invocation has no inbox row to enlist in:
        // it sends through this context, and outgoing messages still need to be stored and forwarded
        Transaction = this;

        // What the failed attempt assigned is rolled back along with its messages: a response it captured
        // must not be returned if the retry produces none, the saga id it resolved must not leak into
        // messages the retry publishes before resolving its own, and the acknowledgement ReadEnvelope
        // queued was discarded with the rest of the outgoing messages, so queue it again
        if (Envelope != null)
        {
            Envelope.Response = null;
            _sagaId = Envelope.SagaId;
            queueRequestedAcknowledgement();
        }
    }

    internal ValueTask ForwardScheduledEnvelopeAsync(Envelope envelope)
    {
        if (envelope.Destination == null)
        {
            throw new InvalidOperationException($"{nameof(Envelope.Destination)} is missing");
        }

        if (envelope.ContentType == null)
        {
            throw new InvalidOperationException("${nameof(Envelope.ContentType} is missing");
        }

        envelope.Sender = Runtime.Endpoints.GetOrBuildSendingAgent(envelope.Destination);
        envelope.Serializer = Runtime.Options.FindSerializer(envelope.ContentType);

        // The sender wire tap is an in-memory-only reference that does not survive the
        // durable serialization round trip a scheduled send goes through. Re-attach it
        // from the resolved destination endpoint so RecordSuccessAsync still fires when
        // the previously scheduled message is finally sent. See GH-3263.
        envelope.WireTap ??= envelope.Sender.Endpoint.WireTap;

        if (envelope.Serializer == null)
        {
            throw new InvalidOperationException($"Invalid content type '{envelope.ContentType}'");
        }

        return PersistOrSendAsync(envelope);
    }

    public async Task EnqueueCascadingAsync(object? message)
    {
        if (message is ISideEffect)
        {
            throw new InvalidOperationException(
                $"Message of type {message.GetType().FullNameInCode()} implements {typeof(ISideEffect).FullNameInCode()}, and cannot be used as a cascading message. Side effects cannot be mixed in with outgoing cascaded messages.");

        }

        if (Envelope?.ResponseType != null && (message?.GetType() == Envelope.ResponseType ||
                                               Envelope.ResponseType.IsInstanceOfType(message)))
        {
            Envelope.Response = message;

            if (Runtime.Options.Durability.Mode == DurabilityMode.MediatorOnly) return;

            // This was done specifically for the HTTP transport's optimized
            // request/reply mechanism
            if (Envelope.DoNotCascadeResponse)
            {
                return;
            }
            else
            {
                await PublishAsync(message).ConfigureAwait(false);
                return;
            }
        }

        switch (message)
        {
            case null:
                return;

            case ISendMyself sendsMyself:
                await sendsMyself.ApplyAsync(this).ConfigureAwait(false);
                return;

            case Envelope _:
                throw new InvalidOperationException(
                    "You cannot directly send an Envelope. You may want to use ISendMyself for cascading messages");

            case IEnumerable<object> enumerable:
                foreach (var o in enumerable) await EnqueueCascadingAsync(o).ConfigureAwait(false);

                return;

            case IAsyncEnumerable<object> asyncEnumerable:
                await foreach (var o in asyncEnumerable) await EnqueueCascadingAsync(o).ConfigureAwait(false);

                return;
        }

        // Handle typed IAsyncEnumerable<T> (T != object) as cascading messages.
        // IAsyncEnumerable<T> is not covariant, so IAsyncEnumerable<SomeType> does not match
        // the case above. When ResponseType is set (StreamAsync path), the check above this
        // switch already captured the sequence; we only reach here during regular InvokeAsync
        // with a handler that returns a typed async sequence.
        var cascader = ResolveTypedAsyncEnumerableCascader(message.GetType());
        if (cascader != null)
        {
            await ((Task)cascader.Invoke(null, [message, this])!).ConfigureAwait(false);
            return;
        }

        if (Envelope?.ReplyUri != null && message.GetType().ToMessageTypeName() == Envelope.ReplyRequested)
        {
            await EndpointFor(Envelope.ReplyUri!).SendAsync(message, new DeliveryOptions { IsResponse = true }).ConfigureAwait(false);

            // If [AlwaysPublishResponse] was used, also publish as a cascading message
            if (Envelope.AlwaysPublishResponse)
            {
                await PublishAsync(message).ConfigureAwait(false);
            }

            return;
        }

        await PublishAsync(message).ConfigureAwait(false);
    }

    private static readonly MethodInfo _cascadeTypedItemsMethod =
        typeof(MessageContext).GetMethod(nameof(CascadeTypedItemsAsync), BindingFlags.Static | BindingFlags.NonPublic)!;

    // Per-message-type cache of the constructed CascadeTypedItemsAsync<T> MethodInfo (or null for
    // message types that don't implement IAsyncEnumerable<T>). Eliminates GetInterfaces() and
    // MakeGenericMethod() from every cascade after the first for each unique type. ImHashMap is
    // lock-free and copy-on-write — appropriate because the set of cascading message types
    // stabilizes quickly after startup, making this write-rare/read-heavy.
    private static ImHashMap<Type, MethodInfo?> _typedEnumerableCascadeMethods =
        ImHashMap<Type, MethodInfo?>.Empty;

    // Resolves the constructed CascadeTypedItemsAsync<T> MethodInfo for a message
    // type that implements IAsyncEnumerable<T>. Steady state hits the ImHashMap
    // cache; the miss path walks GetInterfaces (IL2070) and MakeGenericMethod
    // (IL2060 + IL3050). AOT-clean apps pre-populate the cache during handler-
    // graph compilation: the source-generated handler registration knows which
    // message types implement IAsyncEnumerable<T> and seeds _typedEnumerableCascade
    // Methods, so the miss path never fires at steady state. The cached
    // MethodInfo is invoked dynamically by the cascading dispatch site
    // (line 693), which is also runtime codegen — same suppression rationale.
    //
    // Leaf suppression rather than [RequiresDynamicCode] propagation because
    // this method is on the per-message cascading dispatch hot path through
    // EnqueueCascadingAsync; cascading [Requires*] up there would force every
    // user-facing handler-result API to declare it.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Cached typed-enumerable cascader for runtime messageType; AOT consumers pre-populate the cache at handler-graph compile time. See AOT guide.")]
    [UnconditionalSuppressMessage("Trimming", "IL2060",
        Justification = "Cached typed-enumerable cascader for runtime messageType; AOT consumers pre-populate the cache at handler-graph compile time. See AOT guide.")]
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "messageType reaches GetInterfaces from runtime-resolved message types that are statically rooted via handler registration.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "MakeGenericMethod over CascadeTypedItemsAsync<T>; AOT consumers pre-populate the cache at handler-graph compile time. See AOT guide.")]
    private static MethodInfo? ResolveTypedAsyncEnumerableCascader(Type messageType)
    {
        if (_typedEnumerableCascadeMethods.TryFind(messageType, out var cached))
        {
            return cached;
        }

        var asyncEnumInterface = messageType.GetInterfaces()
            .FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));

        var method = asyncEnumInterface != null
            ? _cascadeTypedItemsMethod.MakeGenericMethod(asyncEnumInterface.GetGenericArguments()[0])
            : null;

        _typedEnumerableCascadeMethods = _typedEnumerableCascadeMethods.AddOrUpdate(messageType, method);
        return method;
    }

    /// <summary>
    /// Pre-populate the typed-enumerable cascader cache with the supplied message
    /// types. Called from <see cref="Wolverine.Runtime.Handlers.HandlerGraph.Compile"/>
    /// after handler-graph compilation so the per-message
    /// <see cref="ResolveTypedAsyncEnumerableCascader"/> hot path never pays the
    /// first-occurrence reflection cost (GetInterfaces walk + MakeGenericMethod).
    /// Closes the AOT story for the cascading-async-enumerable resolution from
    /// AOT pillar issue #2769 — at steady state the cache is pre-warmed and the
    /// reflective miss path inside <see cref="ResolveTypedAsyncEnumerableCascader"/>
    /// never fires.
    /// </summary>
    /// <remarks>
    /// Tolerates duplicates; the underlying <c>ImHashMap.AddOrUpdate</c> is idempotent.
    /// Tolerates a null source for defensive callers.
    /// </remarks>
    /// <param name="messageTypes">Message types to resolve and cache.</param>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Pre-populating the typed-enumerable cache at handler-graph compile time; same suppression as ResolveTypedAsyncEnumerableCascader. See AOT guide / #2769.")]
    [UnconditionalSuppressMessage("Trimming", "IL2060",
        Justification = "Pre-populating the typed-enumerable cache at handler-graph compile time; same suppression as ResolveTypedAsyncEnumerableCascader. See AOT guide / #2769.")]
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Pre-populating the typed-enumerable cache at handler-graph compile time; same suppression as ResolveTypedAsyncEnumerableCascader. See AOT guide / #2769.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Pre-populating the typed-enumerable cache at handler-graph compile time; same suppression as ResolveTypedAsyncEnumerableCascader. See AOT guide / #2769.")]
    internal static void PrepopulateCascadeCache(IEnumerable<Type>? messageTypes)
    {
        if (messageTypes == null) return;

        foreach (var messageType in messageTypes)
        {
            if (messageType == null) continue;
            if (_typedEnumerableCascadeMethods.TryFind(messageType, out _)) continue;

            ResolveTypedAsyncEnumerableCascader(messageType);
        }
    }

    private static async Task CascadeTypedItemsAsync<T>(IAsyncEnumerable<T> source, MessageContext context)
    {
        await foreach (var item in source)
        {
            await context.EnqueueCascadingAsync(item).ConfigureAwait(false);
        }
    }

    internal void ClearState()
    {
        if (Transaction is IDisposable d)
        {
            d.SafeDispose();
        }

        _hasFlushed = false;

        _sent?.Clear();
        lock (_outstandingLock)
        {
            _outstanding.Clear();
        }
        Scheduled.Clear();
        Envelope = null;
        Transaction = null;
        _sagaId = null;
        _rescheduled = false;
        _eagerIdempotencyStamped = false;
        Tracker = null;
    }

    public void SetSagaId(object sagaId) => _sagaId = sagaId;

    internal void ReadEnvelope(Envelope? originalEnvelope, IChannelCallback channel)
    {
        Envelope = originalEnvelope ?? throw new ArgumentNullException(nameof(originalEnvelope));

        // The receiver has already decided which store owns this envelope's inbox row (see
        // DurableReceiver/DurableLocalQueue.assignAncillaryStoreIfNeeded). Point the context at that
        // same store so marking the message handled -- and any outbox work the handler does -- lands
        // in the database that actually holds the row. Without this the context keeps the main store
        // and EfCoreEnvelopeTransaction marks handled against the wrong database, updating zero rows
        // and leaving the envelope Incoming forever. The [MartenStore] path already got this from the
        // outbox frame's OverrideStorage call; a handler that just injects an enrolled DbContext has
        // no such frame. See GH-3870.
        if (originalEnvelope.Store != null)
        {
            Storage = originalEnvelope.Store;
        }

        originalEnvelope.MaybeCorrectReplyUri();

        CorrelationId = originalEnvelope.CorrelationId;
        ConversationId = originalEnvelope.Id;
        _channel = channel;
        _sagaId = originalEnvelope.SagaId;
        _rescheduled = false;
        _eagerIdempotencyStamped = false;
        TenantId = originalEnvelope.TenantId;

        // GH-4640. The setter above runs the value through Durability.TenantIdStyle, so the context is
        // normalised and the envelope was not -- and Polecat and Fisher build their session from
        // `Envelope?.TenantId ?? context.TenantId`, so under ForceLowerCase they wrote the raw casing.
        // On SQL Server's case-insensitive collation that is merely a wrong value in the column; on
        // SQLite's BINARY collation the row is unreachable under the normalised id.
        //
        // Writing the normalised value back here rather than at each assignment site is deliberate:
        // every execution path -- inbound transports, durable and buffered local queues, sharded
        // execution, and inline InvokeAsync/InvokeForTenantAsync via Executor.InvokeInlineAsync --
        // funnels through ReadEnvelope, so one place covers all of them and every reader of
        // Envelope.TenantId agrees with the context. Guarded so a null tenant can never become
        // non-null, and a no-op under the default TenantIdStyle.CaseSensitive.
        if (originalEnvelope.TenantId.IsNotEmpty())
        {
            originalEnvelope.TenantId = TenantId;
        }

        UserName = originalEnvelope.UserName;

        Transaction = this;

        queueRequestedAcknowledgement();
    }

    // The acknowledgement a caller asked for with DeliveryOptions.AckRequested is queued up front as
    // part of the context's baseline, so that handling the message successfully sends it
    private void queueRequestedAcknowledgement()
    {
        if (Envelope!.AckRequested && Envelope.ReplyUri != null)
        {
            var ack = new Acknowledgement { RequestId = Envelope.Id };
            var ackEnvelope = Runtime.RoutingFor(typeof(Acknowledgement))
                .RouteToDestination(ack, Envelope.ReplyUri, null);
            TrackEnvelopeCorrelation(ackEnvelope, Activity.Current);
            lock (_outstandingLock)
            {
                _outstanding.Add(ackEnvelope);
            }
        }
    }

    private async Task flushScheduledMessagesAsync()
    {
        if (Storage is NullMessageStore)
        {
            foreach (var envelope in Scheduled)
            {
                Runtime.Logger.LogDebug("Flushing scheduled envelope {EnvelopeId} ({MessageType}) to in-memory execution (NullMessageStore)", envelope.Id, envelope.MessageType);
                Runtime.ScheduleLocalExecutionInMemory(envelope.ScheduledTime!.Value, envelope);
            }
        }
        else
        {
            foreach (var envelope in Scheduled)
            {
                Runtime.Logger.LogDebug("Flushing scheduled envelope {EnvelopeId} ({MessageType}) to durable inbox for retry scheduling", envelope.Id, envelope.MessageType);
                await Storage.Inbox.RescheduleExistingEnvelopeForRetryAsync(envelope).ConfigureAwait(false);
            }
        }

        Scheduled.Clear();
    }

    internal override void TrackEnvelopeCorrelation(Envelope outbound, Activity? activity)
    {
        base.TrackEnvelopeCorrelation(outbound, activity);

        // Precedence (highest to lowest):
        //   1. An explicit SagaId set on the outbound envelope by the caller
        //      (e.g. via DeliveryOptions.SagaId in OutgoingMessages, or set
        //      directly on the envelope). This must win — a saga's Start
        //      method that generates its own id and tags a cascaded message
        //      with it should not have that explicit value silently
        //      overwritten by the inbound envelope's SagaId or the context's
        //      _sagaId. See GH-2595.
        //   2. The current message context's _sagaId — the saga id resolved
        //      for the message currently being handled (set by saga handler
        //      generated code or by ReadEnvelope from the inbound envelope).
        //   3. The inbound envelope's SagaId as a final fallback.
        outbound.SagaId = outbound.SagaId.IsNotEmpty()
            ? outbound.SagaId
            : (_sagaId?.ToString() ?? Envelope?.SagaId);

        if (ConversationId != Guid.Empty)
        {
            outbound.ConversationId = ConversationId;
        }

        if (Envelope != null)
        {
            outbound.ConversationId = Envelope.ConversationId == Guid.Empty ? Envelope.Id : Envelope.ConversationId;
        }
    }

    public void OverrideStorage(IMessageStore messageStore)
    {
        Storage = messageStore;
    }
}
