using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Transports.Local;

namespace Wolverine.Runtime.Handlers;

/// <summary>
/// GH-5136. Every handler chain of one message type. Wolverine 7 builds one <see cref="HandlerChain"/> per
/// (message type, handler type) -- a <see cref="Wolverine.Persistence.Sagas.SagaChain"/> still holds every
/// method of its one saga type -- and this is the group they belong to. It replaces the empty "placeholder"
/// root chain and the per-endpoint child chains (<c>HandlerChain.ByEndpoint</c>) of 6.x.
/// </summary>
internal class MessageHandlerGroup
{
    private readonly List<HandlerChain> _chains = [];

    public MessageHandlerGroup(Type messageType)
    {
        MessageType = messageType;
    }

    public MessageHandlerGroup(Type messageType, HandlerChain chain) : this(messageType)
    {
        _chains.Add(chain);
    }

    public Type MessageType { get; }

    /// <summary>
    /// The chains of this message type, one per handler type, in discovery order
    /// </summary>
    public IReadOnlyList<HandlerChain> Chains => _chains;

    internal void Add(HandlerChain chain) => _chains.Add(chain);

    /// <summary>
    /// The chains bound to one or more endpoints, either by a sticky handler declaration or, when there is
    /// more than one chain, by the per handler type local queue
    /// </summary>
    public IEnumerable<HandlerChain> BoundChains() => _chains.Where(x => x.Endpoints.Count > 0);

    /// <summary>
    /// The chains that run wherever the message type is received, which is at most one: the message type's
    /// only chain when it is not sticky
    /// </summary>
    public IEnumerable<HandlerChain> UnboundChains() => _chains.Where(x => x.Endpoints.Count == 0);

    /// <summary>
    /// Every distinct local queue a chain of this message type is bound to
    /// </summary>
    public Uri[] BoundLocalQueueUris() => BoundChains()
        .SelectMany(c => c.Endpoints)
        .OfType<LocalQueue>()
        .Select(e => e.Uri)
        .Distinct()
        .ToArray();

    /// <summary>
    /// The handler type of one chain of this group. Read from the saga type for a saga chain, which clears its
    /// handler calls once it has generated code
    /// </summary>
    internal static Type HandlerTypeOf(HandlerChain chain)
    {
        if (chain is Wolverine.Persistence.Sagas.SagaChain { SagaType: { } sagaType }) return sagaType;

        return chain.HandlerCalls().First().HandlerType;
    }

    internal string DescribeHandlerTypes() => Describe(_chains);

    /// <summary>
    /// GH-5137. Each handler type of this message type and the endpoints its chain is bound to
    /// </summary>
    internal string DescribeBindings() => _chains
        .Select(x => $"{HandlerTypeOf(x).FullNameInCode()} ({(x.Endpoints.Count == 0 ? "anywhere" : x.Endpoints.Select(e => e.Uri.ToString()).Join(", "))})")
        .Join(", ");

    internal static string Describe(IEnumerable<HandlerChain> chains) => chains
        .Select(HandlerTypeOf)
        .Select(x => x.FullNameInCode())
        .Distinct()
        .Join(", ");
}

/// <summary>
/// GH-5136. Thrown when code asks for "the" handler chain of a message type that has more than one handler
/// type, and therefore more than one chain
/// </summary>
public class AmbiguousHandlerChainException : Exception
{
    internal AmbiguousHandlerChainException(Type messageType, IReadOnlyList<HandlerChain> chains) : base(
        $"Message type {messageType.FullNameInCode()} is handled by {chains.Count} handler chains, one per handler type " +
        $"({MessageHandlerGroup.Describe(chains)}), " +
        "so there is no single chain for it. Look up every chain of the message type with HandlerGraph.AllChains() instead.")
    {
        MessageType = messageType;
    }

    public Type MessageType { get; }
}
