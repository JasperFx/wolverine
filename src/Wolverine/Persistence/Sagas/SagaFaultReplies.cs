using ImTools;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// GH-5553. Which messages a saga asks for the <see cref="Fault{T}" /> of: every <c>T</c> it has a handler for
/// <c>Fault&lt;T&gt;</c> of. Keyed by the message the saga is handling when it sends, so the outgoing message is
/// stamped with <see cref="FaultHeaders.ReplyTo" /> while the saga's own message is in hand. Worked out once, when the
/// handler graph is compiled, then narrowed once the transports have started: a <c>T</c> whose <c>Fault&lt;T&gt;</c>
/// the application already receives through a route or a listener of its own is left out, so it keeps getting that
/// fault exactly as it did before. The send path only reads it
/// </summary>
internal sealed class SagaFaultReplies
{
    public static readonly SagaFaultReplies None = new(ImHashMap<Type, Type[]>.Empty, false);

    private readonly ImHashMap<Type, Type[]> _byIncoming;
    private readonly bool _any;

    private SagaFaultReplies(ImHashMap<Type, Type[]> byIncoming, bool any)
    {
        _byIncoming = byIncoming;
        _any = any;
    }

    public static SagaFaultReplies Build(IEnumerable<SagaChain> sagaChains)
    {
        var chains = sagaChains.ToArray();

        var requestedBySaga = chains
            .Where(x => x.MessageType.IsGenericType && x.MessageType.GetGenericTypeDefinition() == typeof(Fault<>))
            .GroupBy(x => x.SagaType)
            .ToDictionary(x => x.Key, x => x.Select(c => c.MessageType.GetGenericArguments()[0]).Distinct().ToArray());

        if (requestedBySaga.Count == 0) return None;

        var byIncoming = new Dictionary<Type, HashSet<Type>>();
        foreach (var chain in chains)
        {
            if (!requestedBySaga.TryGetValue(chain.SagaType, out var requested)) continue;

            if (!byIncoming.TryGetValue(chain.MessageType, out var set))
            {
                set = [];
                byIncoming[chain.MessageType] = set;
            }

            set.UnionWith(requested);
        }

        var map = ImHashMap<Type, Type[]>.Empty;
        foreach (var pair in byIncoming) map = map.AddOrUpdate(pair.Key, pair.Value.ToArray());

        return new SagaFaultReplies(map, byIncoming.Count > 0);
    }

    /// <summary>
    /// Every message type some saga asks for the fault of
    /// </summary>
    public Type[] RequestedMessageTypes()
        => _byIncoming.Enumerate().SelectMany(x => x.Value).Distinct().ToArray();

    /// <summary>
    /// The same requests without <paramref name="messageTypes" />: the faults of those the application already
    /// receives through a route or listener of its own, so asking again would deliver them twice
    /// </summary>
    public SagaFaultReplies Without(IReadOnlySet<Type> messageTypes)
    {
        if (messageTypes.Count == 0) return this;

        var map = ImHashMap<Type, Type[]>.Empty;
        var any = false;
        foreach (var entry in _byIncoming.Enumerate())
        {
            var remaining = entry.Value.Where(x => !messageTypes.Contains(x)).ToArray();
            if (remaining.Length == 0) continue;

            map = map.AddOrUpdate(entry.Key, remaining);
            any = true;
        }

        return any ? new SagaFaultReplies(map, true) : None;
    }

    /// <summary>
    /// Does the saga handling <paramref name="incoming" /> ask for the fault of an outgoing <paramref name="outgoing" />?
    /// </summary>
    public bool IsRequested(Type incoming, Type outgoing)
        => _any && _byIncoming.TryFind(incoming, out var requested) && Array.IndexOf(requested, outgoing) >= 0;
}
