using System.Security.Cryptography;
using System.Text;
using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Routing;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Util;

namespace Wolverine.Runtime.Partitioning;

/// <summary>
/// GH-5695. The handler types that run a message of a partitioned topology on one of the topology's slots, and where
/// the others are relayed to
/// </summary>
/// <param name="Group">The message type's handler group</param>
/// <param name="Members">The chains that run on the slot, one after the other, in discovery order</param>
/// <param name="Relays">The endpoints of the sticky handler types that keep running on their own endpoints</param>
internal record PartitionedSlotMembers(MessageHandlerGroup Group, HandlerChain[] Members, Uri[] Relays);

/// <summary>
/// GH-5695. Since 7.0 every handler type of a message type has its own handler chain and its own local queue, so a slot
/// of a partitioned topology could only relay a message with several handler types to those queues and move on, and
/// nothing kept a group's messages in order across them. Instead, the slot's execution lane now expands such a message,
/// when it receives it, into one copy per handler type and runs the copies there, one after the other in handler
/// discovery order, before it takes the next envelope. A lane runs one envelope at a time, so the copies of one message
/// are never interleaved with another message of the same group, however many publishers there are
/// </summary>
internal static class PartitionedSlotExpansion
{
    /// <summary>
    /// The value of <see cref="EnvelopeConstants.PartitionedHandlerTypeKey"/> for a copy that runs in
    /// <paramref name="handlerType"/>
    /// </summary>
    internal static string KeyFor(Type handlerType) => handlerType.FullName ?? handlerType.Name;

    internal static string KeyFor(HandlerChain chain) => KeyFor(MessageHandlerGroup.HandlerTypeOf(chain));

    /// <summary>
    /// Whether a slot in this mode runs every handler type of a message type itself. A buffered slot does, and so does a
    /// durable slot, which stores the copies as inbox rows before it runs them. A native-ack slot would have to hold the
    /// broker delivery until every copy settled, and an inline endpoint doesn't shard by group at all, so both still
    /// refuse a message type with several handler types at startup
    /// </summary>
    internal static bool Supports(Endpoint slot) => slot.Mode is EndpointMode.BufferedInMemory or EndpointMode.Durable;

    /// <summary>
    /// A handler type runs on the slot unless it is sticky to an endpoint of its own. A sticky handler type keeps
    /// running on that endpoint, unordered, unless it opts in with <see cref="RunOnPartitionSlotAttribute"/>
    /// </summary>
    internal static bool RunsOnSlot(HandlerChain chain) =>
        !chain.IsSticky || chain.HasAttribute<RunOnPartitionSlotAttribute>();

    /// <summary>
    /// The local or external sharded topology that <paramref name="endpoint"/> is a listening slot of, and that
    /// publishes <paramref name="messageType"/>
    /// </summary>
    internal static PartitionedMessageTopology? TopologyOf(MessagePartitioningRules rules, Type messageType,
        Endpoint endpoint)
    {
        if (!endpoint.UsedInShardedTopology) return null;

        return rules.ShardedMessageTopologies.FirstOrDefault(x =>
            x.ListensLocally && x.Slots.Contains(endpoint) && x.Matches(messageType));
    }

    /// <summary>
    /// What runs a message of <paramref name="messageType"/> received at the slot <paramref name="endpoint"/>, or null
    /// when the slot doesn't need to expand it: a message type with a single handler type that runs on the slot, or one
    /// whose handler types are all sticky elsewhere, keeps today's handler resolution. With several topologies
    /// publishing the message type (GH-5916), a topology's members are the handler types it declares with
    /// <c>HandledBy()</c>
    /// </summary>
    internal static PartitionedSlotMembers? MembersFor(MessagePartitioningRules rules, HandlerGraph handlers,
        Type messageType, Endpoint endpoint)
    {
        if (handlers.GroupFor(messageType) is not { Chains.Count: > 0 } group) return null;

        var topology = TopologyOf(rules, messageType, endpoint);
        if (topology == null || !Supports(endpoint)) return null;

        var chains = rules.HandlerScopes.ChainsRunBy(topology, group);
        var members = chains.Where(RunsOnSlot).ToArray();
        var relays = chains.Where(x => !RunsOnSlot(x)).SelectMany(x => x.Endpoints).Select(x => x.Uri).Distinct()
            .ToArray();

        // Nothing to run on the slot or relay from it
        if (members.Length == 0 && relays.Length == 0) return null;

        // A message type whose only handler type is sticky elsewhere keeps today's relay to it (GH-5763)
        if (members.Length == 0 && group.Chains.Count == 1) return null;

        // One handler type that isn't sticky, and nothing to relay: it runs right on the slot, as it always has
        if (members.Length == 1 && relays.Length == 0 && !members[0].IsSticky) return null;

        return new PartitionedSlotMembers(group, members, relays);
    }

    // A fixed namespace for the name-based ids of the copies (RFC 4122 version 5)
    private static readonly byte[] CopyIdNamespace = new Guid("2c1e3d0f-7b8a-4f53-9d6e-56951a7d0c42").ToByteArray();

    /// <summary>
    /// The id of the copy of the envelope <paramref name="originalId"/> for the handler type
    /// <paramref name="handlerTypeKey"/>: a name-based (version 5) GUID, so expanding the same envelope again always
    /// produces the same ids. Each copy needs an id of its own, because the copies share the slot as their destination,
    /// and a shared id would collide in the inbox under either inbox identity
    /// </summary>
    internal static Guid CopyIdFor(Guid originalId, string handlerTypeKey)
    {
        var name = Encoding.UTF8.GetBytes(originalId.ToString("N") + "/" + handlerTypeKey);
        var input = new byte[CopyIdNamespace.Length + name.Length];
        CopyIdNamespace.CopyTo(input, 0);
        name.CopyTo(input, CopyIdNamespace.Length);

#pragma warning disable CA5350 // SHA-1 is what RFC 4122 specifies for name-based ids; this is not a security use
        var hash = SHA1.HashData(input);
#pragma warning restore CA5350

        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes);
    }

    /// <summary>
    /// The copy of <paramref name="envelope"/> for one member. A requested reply stays only on the copies whose handler
    /// type can produce it, so the other copies don't fail for not answering a request meant for one of them
    /// </summary>
    internal static Envelope CopyFor(Envelope envelope, HandlerChain member)
    {
        var key = KeyFor(member);
        var copy = envelope.CloneForPartitionedHandler(CopyIdFor(envelope.Id, key), key);

        if (envelope.ReplyRequested.IsNotEmpty() && !produces(member, envelope.ReplyRequested!))
        {
            copy.ReplyRequested = null;
        }

        return copy;
    }

    private static bool produces(HandlerChain chain, string replyMessageType)
    {
        return MultipleChainInvoker.ResponseTypesOf(chain).Any(x => x.ToMessageTypeName() == replyMessageType);
    }
}

/// <summary>
/// GH-5695. The executor of a message type with several handler types on a partitioned topology's slot:
/// <list type="bullet">
/// <item>an envelope naming its handler type in <see cref="EnvelopeConstants.PartitionedHandlerTypeKey"/> -- a copy
/// being run, retried, requeued, rescheduled or replayed from the dead letter queue -- runs in that handler type's
/// chain and nothing else. One naming a handler type that isn't a member here is dead-lettered</item>
/// <item>any other envelope is expanded into one copy per member, which run one after the other, each through the
/// pipeline in its own context, scope and transaction, before the lane takes its next envelope. The sticky handler
/// types that stay on their own endpoints get the message relayed to them</item>
/// </list>
/// </summary>
internal class PartitionedGroupExecutor : IExecutor
{
    private readonly WolverineRuntime _runtime;
    private readonly PartitionedSlotMembers _members;
    private readonly IMessageTracker _tracker;
    private readonly (HandlerChain Chain, IExecutor Executor)[] _ordered;
    private readonly MultipleChainInvoker _invoker;

    // Built once, here: every lookup on the hot path is a lock-free read
    private readonly ImHashMap<string, IExecutor> _byKey = ImHashMap<string, IExecutor>.Empty;

    public PartitionedGroupExecutor(WolverineRuntime runtime, PartitionedSlotMembers members,
        Func<HandlerChain, IExecutor> executorFor, IMessageTracker tracker)
    {
        _runtime = runtime;
        _members = members;
        _tracker = tracker;

        _ordered = members.Members.Select(chain => (chain, executorFor(chain))).ToArray();
        foreach (var (chain, executor) in _ordered)
        {
            _byKey = _byKey.AddOrUpdate(PartitionedSlotExpansion.KeyFor(chain), executor);
        }

        _invoker = new MultipleChainInvoker(members.Group, executorFor);
    }

    public Type MessageType => _members.Group.MessageType;

    /// <summary>
    /// GH-5695. A circuit breaker tracks the outcome of every copy
    /// </summary>
    internal PartitionedGroupExecutor WrapWithMessageTracking(IMessageSuccessTracker tracker)
    {
        var wrapped = _ordered.ToDictionary(x => x.Chain,
            x => x.Executor is Executor e ? e.WrapWithMessageTracking(tracker) : x.Executor);

        return new PartitionedGroupExecutor(_runtime, _members, chain => wrapped[chain], _tracker);
    }

    public Task<IContinuation> ExecuteAsync(MessageContext context, CancellationToken cancellation)
    {
        var envelope = context.Envelope!;
        if (envelope.TryGetHeader(EnvelopeConstants.PartitionedHandlerTypeKey, out var key))
        {
            if (_byKey.TryFind(key!, out var executor))
            {
                return executor.ExecuteAsync(context, cancellation);
            }

            return Task.FromResult<IContinuation>(
                new MoveToErrorQueue(new UnknownPartitionedHandlerTypeException(envelope, key!, _members)));
        }

        return expandAsync(context);
    }

    private async Task<IContinuation> expandAsync(MessageContext context)
    {
        context.Tracker = _tracker;

        var envelope = context.Envelope!;
        _tracker.ExecutionStarted(envelope);
        envelope.Attempts++;

        try
        {
            var channel = context.Channel ?? throw new InvalidOperationException(
                $"Envelope {envelope.Id} ({envelope.MessageType}) has no channel to run its copies on");

            if (_members.Relays.Length > 0)
            {
                await relayToStickyHandlersAsync(context, envelope).ConfigureAwait(false);
            }

            var pipeline = channel.Pipeline ?? _runtime.Pipeline;

            IReadOnlyList<Envelope> copies = _ordered.Select(x => PartitionedSlotExpansion.CopyFor(envelope, x.Chain)).ToArray();

            // A durable slot stores the copies as inbox rows, and marks the original handled, before any of them runs.
            // A copy that already had a row was expanded before a crash, and is left to that row
            var durable = channel as DurableReceiver;
            if (durable != null)
            {
                copies = await durable.StoreExpandedCopiesAsync(envelope, copies).ConfigureAwait(false);
            }

            foreach (var copy in copies)
            {
                try
                {
                    // The pipeline runs nothing once the host is stopping. A durable copy's row is still owned here, so
                    // it is handed back to be recovered rather than left behind
                    if (_runtime.Cancellation.IsCancellationRequested)
                    {
                        durable?.AbandonExpandedCopy(copy);
                        continue;
                    }

                    _tracker.Sent(copy);

                    // Awaited, in the lane: nothing else in this lane runs until the copy has settled -- succeeded,
                    // been dead-lettered or discarded, or left the line to be retried later. An inline retry holds the
                    // lane
                    await pipeline.InvokeAsync(copy, channel).ConfigureAwait(false);
                }
                finally
                {
                    durable?.LeaveExpandedCopy(copy);
                }
            }

            _tracker.ExecutionFinished(envelope);
            return MessageSucceededContinuation.Instance;
        }
        catch (Exception e)
        {
            _tracker.ExecutionFinished(envelope, e);
            return _runtime.Handlers.Failures.DetermineExecutionContinuation(e, envelope);
        }
    }

    private async Task relayToStickyHandlersAsync(MessageContext context, Envelope envelope)
    {
        DeliveryOptions? options = null;
        if (envelope.HasHeaders)
        {
            options = new DeliveryOptions();
            foreach (var header in envelope.Headers)
            {
                options.WithHeader(header.Key, header.Value ?? string.Empty);
            }
        }

        foreach (var uri in _members.Relays)
        {
            await context.EndpointFor(uri).SendAsync(envelope.Message, options).ConfigureAwait(false);
        }
    }

    public Task<InvokeResult> InvokeAsync(MessageContext context, CancellationToken cancellation)
    {
        if (context.Envelope != null &&
            context.Envelope.TryGetHeader(EnvelopeConstants.PartitionedHandlerTypeKey, out var key) &&
            _byKey.TryFind(key!, out var executor))
        {
            return executor.InvokeAsync(context, cancellation);
        }

        throw new NotSupportedException(
            $"A message of type {MessageType.FullNameInCode()} received at a partitioned topology's slot is run one handler type at a time");
    }

    public async Task InvokeInlineAsync(Envelope envelope, CancellationToken cancellation)
    {
        if (envelope.TryGetHeader(EnvelopeConstants.PartitionedHandlerTypeKey, out var key))
        {
            if (!_byKey.TryFind(key!, out var executor))
            {
                throw new UnknownPartitionedHandlerTypeException(envelope, key!, _members);
            }

            await executor.InvokeInlineAsync(envelope, cancellation).ConfigureAwait(false);
            return;
        }

        foreach (var (chain, executor) in _ordered)
        {
            await executor.InvokeInlineAsync(PartitionedSlotExpansion.CopyFor(envelope, chain), cancellation)
                .ConfigureAwait(false);
        }
    }

    public Task<T> InvokeAsync<T>(object message, MessageBus bus, CancellationToken cancellation = default,
        TimeSpan? timeout = null, DeliveryOptions? options = null)
    {
        return _invoker.InvokeAsync<T>(message, bus, cancellation, timeout, options);
    }

    public Task InvokeAsync(object message, MessageBus bus, CancellationToken cancellation = default,
        TimeSpan? timeout = null, DeliveryOptions? options = null)
    {
        return _invoker.InvokeAsync(message, bus, cancellation, timeout, options);
    }

    public IAsyncEnumerable<T> StreamAsync<T>(object message, MessageBus bus, CancellationToken cancellation = default,
        DeliveryOptions? options = null)
    {
        return _invoker.StreamAsync<T>(message, bus, cancellation, options);
    }
}

/// <summary>
/// GH-5695. A copy of a partitioned message names a handler type that doesn't run on this slot: the handler type was
/// renamed or removed since the copy was made, or it no longer belongs to this topology. The copy is dead-lettered
/// with this exception rather than guessed at
/// </summary>
public class UnknownPartitionedHandlerTypeException : Exception
{
    internal UnknownPartitionedHandlerTypeException(Envelope envelope, string handlerType, PartitionedSlotMembers members)
        : base($"Envelope {envelope.Id} of message type {members.Group.MessageType.FullNameInCode()} is a copy for " +
               $"handler type '{handlerType}' (header '{EnvelopeConstants.PartitionedHandlerTypeKey}'), but that handler " +
               "type does not run this message type on the partitioned topology slot " +
               $"{envelope.Destination?.ToString() ?? "it was received at"}. The handler types that do are " +
               $"{members.Members.Select(PartitionedSlotExpansion.KeyFor).Join(", ")}. The handler type was probably " +
               "renamed or removed after the copy was made. Replay it once the handler type is back, or delete it.")
    {
        HandlerType = handlerType;
        MessageType = members.Group.MessageType;
    }

    /// <summary>
    /// The handler type the copy names
    /// </summary>
    public string HandlerType { get; }

    /// <summary>
    /// The message type of the copy
    /// </summary>
    public Type MessageType { get; }
}
