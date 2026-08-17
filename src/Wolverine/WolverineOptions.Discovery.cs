using Wolverine.Runtime.Batching;

namespace Wolverine;

/// <summary>
///     GH-3974. How a batched element type reaches its handler.
/// </summary>
/// <param name="ElementType">The message type being batched, i.e. what a producer actually publishes.</param>
/// <param name="BatchMessageType">
///     The message type the assembled batch is handled as, taken from
///     <see cref="IMessageBatcher.BatchMessageType" />.
/// </param>
/// <remarks>
///     This exists because <c>BatchMessageType</c> is a free-form <see cref="Type" />. The default batcher
///     produces <c>T[]</c>, but nothing requires that, and the auto-swap in <c>WolverineRuntime.HostService</c>
///     deliberately leaves an application-supplied <see cref="IMessageBatcher" /> alone precisely so it can
///     produce its own type — a batcher assembling <c>ServiceUpdateBatch(string Id, ServiceUpdates[] Updates)</c>
///     is fully supported. Consumers were left inferring the relationship from array-ness
///     (<c>parameters[0].ParameterType.GetElementType()</c>), which is silently wrong for exactly those
///     batchers.
/// </remarks>
public sealed record MessageBatchMapping(Type ElementType, Type BatchMessageType);

/// <summary>
///     GH-3974. The message types handler discovery actually resolved, handed to callbacks registered with
///     <see cref="WolverineOptions.OnHandlersDiscovered" />.
/// </summary>
public sealed class DiscoveredHandlers
{
    private readonly HashSet<Type> _messageTypes;

    internal DiscoveredHandlers(IEnumerable<Type> messageTypes)
    {
        _messageTypes = messageTypes.ToHashSet();
    }

    /// <summary>
    ///     Every message type that will be handled by this application.
    /// </summary>
    public IReadOnlyCollection<Type> MessageTypes => _messageTypes;

    /// <summary>
    ///     Will this message type be handled? Ask this instead of re-implementing Wolverine's discovery
    ///     convention by reflection — a mirror of the convention drifts, and it drifts silently.
    /// </summary>
    public bool Handles(Type messageType) => _messageTypes.Contains(messageType);

    /// <summary>
    ///     Will this message type be handled?
    /// </summary>
    public bool Handles<T>() => Handles(typeof(T));
}

public sealed partial class WolverineOptions
{
    private readonly List<Action<DiscoveredHandlers>> _handlerDiscoveryCallbacks = [];

    /// <summary>
    ///     GH-3974. The element type → batch message type mapping for every <c>BatchMessagesOf</c> definition,
    ///     so a consumer can discover how a batched message type is actually handled instead of inferring it
    ///     from the handler parameter being an array.
    /// </summary>
    public IReadOnlyList<MessageBatchMapping> BatchMappings =>
        BatchDefinitions.Select(x => new MessageBatchMapping(x.ElementType, x.Batcher.BatchMessageType)).ToList();

    /// <summary>
    ///     GH-3974. Find the message type that batches of <paramref name="elementType" /> are handled as.
    /// </summary>
    /// <remarks>
    ///     Prefer this over assuming <c>T[]</c>. A custom <see cref="IMessageBatcher" /> may assemble any type
    ///     it likes, and Wolverine deliberately does not override one that an application supplied.
    /// </remarks>
    public bool TryFindBatchMessageType(Type elementType, out Type batchMessageType)
    {
        foreach (var definition in BatchDefinitions)
        {
            if (definition.ElementType == elementType)
            {
                batchMessageType = definition.Batcher.BatchMessageType;
                return true;
            }
        }

        batchMessageType = null!;
        return false;
    }

    /// <summary>
    ///     GH-3974. Register a callback that runs once handler discovery has resolved, receiving the message
    ///     types that will actually be handled.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Discovery and the static <c>TypeLoadMode</c> registry both materialize after options time, so
    ///         "will this message type have a handler?" cannot be answered while <see cref="WolverineOptions" />
    ///         is still being configured. Extensions and app-level conventions that install <i>fallback</i>
    ///         handlers — a relay, a batch forwarder, a catch-all — were therefore hand-rolling a mirror of
    ///         Wolverine's own discovery convention and asking that.
    ///     </para>
    ///     <para>
    ///         Any reflection-based reimplementation of the framework's convention will drift from it, and the
    ///         drift is silent: a mirror that scanned only one assembly stopped seeing handlers that moved to a
    ///         second one, and installed a bare relay <i>over</i> a real handler — the exact defect the guard
    ///         existed to prevent, with every codegen test still passing. Ask the framework instead.
    ///     </para>
    /// </remarks>
    public void OnHandlersDiscovered(Action<DiscoveredHandlers> callback)
    {
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        _handlerDiscoveryCallbacks.Add(callback);
    }

    internal void ApplyHandlerDiscoveryCallbacks(IEnumerable<Type> messageTypes)
    {
        if (_handlerDiscoveryCallbacks.Count == 0) return;

        var discovered = new DiscoveredHandlers(messageTypes);
        foreach (var callback in _handlerDiscoveryCallbacks)
        {
            callback(discovered);
        }
    }
}
