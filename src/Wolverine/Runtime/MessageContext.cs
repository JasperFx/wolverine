using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using JasperFx;
using JasperFx.MultiTenancy;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Transports;
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
    /// <summary>
    /// Ambient holder for the <see cref="MessageContext"/> currently driving the in-flight
    /// handler invocation on this async flow. Set by <c>ServiceLocationAwareExecutor</c> when
    /// a chain is known (at codegen time) to use service location, and consulted by the
    /// <see cref="IMessageContext"/> / <see cref="IMessageBus"/> scoped DI registrations so
    /// that service-located instances see the same <see cref="MessageContext"/> the handler
    /// itself received — preserving outbox semantics.
    ///
    /// Chains that do not use service location never set this value, so the per-message
    /// <see cref="System.Threading.AsyncLocal{T}"/> machinery and ExecutionContext clone
    /// cost are avoided on the hot path. See issue #2583.
    /// </summary>
    private static readonly System.Threading.AsyncLocal<MessageContext?> _current = new();

    /// <summary>
    /// The <see cref="MessageContext"/> driving the current handler invocation, if any.
    /// Set by <c>ServiceLocationAwareExecutor</c>; <see langword="null"/> outside of a
    /// service-location-aware handler invocation. Public so that custom service registrations
    /// can opt into the same ambient handoff.
    /// </summary>
    public static MessageContext? Current
    {
        get => _current.Value;
        internal set => _current.Value = value;
    }

    private IChannelCallback? _channel;

    private bool _hasFlushed;
    private object? _sagaId;

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

        var check = await Transaction.TryMakeEagerIdempotencyCheckAsync(Envelope, Runtime.Options.Durability, cancellation).ConfigureAwait(false);
        if (!check)
        {
            throw new DuplicateIncomingEnvelopeException(Envelope);
        }

        Envelope.WasPersistedInInbox = true;
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

    public async Task FlushOutgoingMessagesAsync()
    {
        if (_hasFlushed)
        {
            switch (MultiFlushMode)
            {
                case MultiFlushMode.OnlyOnce:
                    return;

                case MultiFlushMode.AllowMultiples:
                    Runtime.Logger.LogDebug("Received multiple calls to FlushOutgoingMessagesAsync() to a single MessageContext");
                    break;

                case MultiFlushMode.AssertOnMultiples:
                    throw new InvalidOperationException(
                        $"This MessageContext does not allow multiple calls to {nameof(FlushOutgoingMessagesAsync)} because {nameof(MultiFlushMode)} = {MultiFlushMode}");
            }
        }

        await AssertAnyRequiredResponseWasGenerated().ConfigureAwait(false);

        // Snapshot under lock so concurrent publishes from a Marten projection
        // (Block parallelism = 10 in AggregationRunner) cannot corrupt the list
        // while we're iterating it. GH-2529.
        Envelope[] outgoing;
        lock (_outstandingLock)
        {
            if (_outstanding.Count == 0) return;
            outgoing = _outstanding.ToArray();
        }

        foreach (var envelope in outgoing)
        {
            // https://github.com/JasperFx/wolverine/issues/2006
            if (envelope == null) continue;

            try
            {
                if (envelope.IsScheduledForLater(DateTimeOffset.UtcNow))
                {
                    if (!envelope.Sender!.IsDurable)
                    {
                        if (envelope.Sender!.SupportsNativeScheduledSend)
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
                Runtime.Logger.LogError(e,
                    "Unable to send an outgoing message, most likely due to serialization issues");
                Runtime.MessageTracking.DiscardedEnvelope(envelope);
            }
        }

        if (ReferenceEquals(Transaction, this))
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
                Runtime.Logger.LogInformation("Sending requested reply of type {MessageType} to reply-uri {ReplyUri}", Envelope!.ReplyRequested, Envelope.ReplyUri);
            }
        }
    }

    public async ValueTask CompleteAsync()
    {
        if (_channel == null || Envelope == null)
        {
            throw new InvalidOperationException("No Envelope is active for this context");
        }

        if (Envelope.HasBeenAcked) return;

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
    }

    private ISupportNativeScheduling? tryGetRescheduler(IChannelCallback? channel, Envelope e)
    {
        // TODO: is that ok, or should we modify Task ISupportNativeScheduling.MoveToScheduledUntilAsync(Envelope envelope, DateTimeOffset time) in DurableReceiver and BufferedReceiver?
        if (e.Listener is ISupportNativeScheduling c2)
        {
            return c2;
        }

        if (channel is ISupportNativeScheduling c)
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

        var deadLetterQueue = tryGetDeadLetterQueue(_channel, Envelope);
        if (deadLetterQueue is not null)
        {
            if (Envelope.Batch != null)
            {
                foreach (var envelope in Envelope.Batch)
                {
                    await deadLetterQueue.MoveToErrorsAsync(envelope, exception).ConfigureAwait(false);
                }
            }
            else
            {
                await deadLetterQueue.MoveToErrorsAsync(Envelope, exception).ConfigureAwait(false);
            }

            return;
        }

        if (Envelope.Batch != null)
        {
            foreach (var envelope in Envelope.Batch)
            {
                await Storage.Inbox.MoveToDeadLetterStorageAsync(envelope, exception).ConfigureAwait(false);
            }
        }
        else
        {
            // If persistable, persist
            await Storage.Inbox.MoveToDeadLetterStorageAsync(Envelope, exception).ConfigureAwait(false);
        }

        // If this is Inline
        await _channel.CompleteAsync(Envelope).ConfigureAwait(false);
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
            // This should never happen because all the sending agents catch errors, but you know...
            Runtime.Logger.LogError(e, "Failure while sending an acknowledgement for envelope {Id}", envelope.Id);
        }
    }

    public async ValueTask SendFailureAcknowledgementAsync(string failureDescription)
    {
        if (Envelope!.ReplyUri == null)
        {
            return;
        }

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
            // Should never happen, but still.
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

        Transaction = null;
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
    }

    public void SetSagaId(object sagaId) => _sagaId = sagaId;

    internal void ReadEnvelope(Envelope? originalEnvelope, IChannelCallback channel)
    {
        Envelope = originalEnvelope ?? throw new ArgumentNullException(nameof(originalEnvelope));

        originalEnvelope.MaybeCorrectReplyUri();

        CorrelationId = originalEnvelope.CorrelationId;
        ConversationId = originalEnvelope.Id;
        _channel = channel;
        _sagaId = originalEnvelope.SagaId;
        TenantId = originalEnvelope.TenantId;
        UserName = originalEnvelope.UserName;

        Transaction = this;

        if (Envelope.AckRequested && Envelope.ReplyUri != null)
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
