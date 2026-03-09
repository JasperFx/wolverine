using System.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime.Routing;

namespace Wolverine.Runtime.Partitioning;

public class GlobalPartitionedMessageTopology
{
    private readonly WolverineOptions _options;
    private readonly List<Subscription> _subscriptions = new();
    private PartitionedMessageTopology? _externalTopology;
    private LocalPartitionedMessageTopology? _localTopology;

    public GlobalPartitionedMessageTopology(WolverineOptions options)
    {
        _options = options;
    }

    internal PartitionedMessageTopology? ExternalTopology => _externalTopology;
    internal LocalPartitionedMessageTopology? LocalTopology => _localTopology;

    internal void SetExternalTopology(Func<WolverineOptions, PartitionedMessageTopology> factory, string baseName)
    {
        SetExternalTopology(factory(_options), baseName);
    }

    internal void SetExternalTopology(PartitionedMessageTopology topology, string baseName)
    {
        _externalTopology = topology;

        // Create companion local topology with matching slot count
        var localBaseName = $"global-{baseName}";
        var slotCount = topology.Slots.Count;
        _localTopology = new LocalPartitionedMessageTopology(_options, localBaseName, slotCount);

        // Force durable mode on all external endpoints
        foreach (var slot in topology.Slots)
        {
            slot.Mode = EndpointMode.Durable;
        }

        // Force durable mode on all local endpoints
        foreach (var slot in _localTopology.Slots)
        {
            slot.Mode = EndpointMode.Durable;
        }

        // Tag each external slot endpoint with its companion local queue URI
        for (var i = 0; i < topology.Slots.Count; i++)
        {
            topology.Slots[i].GlobalPartitionLocalQueueUri = _localTopology.Slots[i].Uri;
        }
    }

    /// <summary>
    ///     Create a publishing rule for a single message type
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public void Message<T>()
    {
        Message(typeof(T));
    }

    /// <summary>
    ///     Create a publishing rule for a single message type
    /// </summary>
    /// <param name="type"></param>
    public void Message(Type type)
    {
        _subscriptions.Add(Subscription.ForType(type));
    }

    /// <summary>
    ///     Publish all messages implementing a marker interface or inheriting from a common
    ///     base class
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public void MessagesImplementing<T>()
    {
        _subscriptions.Add(new Subscription { BaseType = typeof(T), Scope = RoutingScope.Implements });
    }

    /// <summary>
    ///     Create a publishing rule for all message types from within the
    ///     specified namespace
    /// </summary>
    /// <param name="namespace"></param>
    public void MessagesFromNamespace(string @namespace)
    {
        _subscriptions.Add(new Subscription
        {
            Match = @namespace,
            Scope = RoutingScope.Namespace
        });
    }

    /// <summary>
    ///     Create a publishing rule for all message types from within the
    ///     namespace holding the marker type "T"
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public void MessagesFromNamespaceContaining<T>()
    {
        MessagesFromNamespace(typeof(T).Namespace!);
    }

    /// <summary>
    ///     Create a publishing rule for all messages from the given assembly
    /// </summary>
    /// <param name="assembly"></param>
    public void MessagesFromAssembly(Assembly assembly)
    {
        _subscriptions.Add(new Subscription(assembly));
    }

    /// <summary>
    ///     Create a publishing rule for all messages from the given assembly that contains the type T
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public void MessagesFromAssemblyContaining<T>()
    {
        MessagesFromAssembly(typeof(T).Assembly);
    }

    public void AssertValidity()
    {
        if (!_subscriptions.Any())
        {
            throw new InvalidOperationException(
                "At least one message type matching policy is required for global partitioning");
        }

        if (_externalTopology == null)
        {
            throw new InvalidOperationException(
                "An external transport topology must be configured for global partitioning");
        }
    }

    internal bool Matches(Type messageType)
    {
        return _subscriptions.Any(x => x.Matches(messageType));
    }

    internal bool TryMatch(Type messageType, IWolverineRuntime runtime, out IMessageRoute? route)
    {
        route = default;

        if (!Matches(messageType))
        {
            return false;
        }

        if (_externalTopology == null || _localTopology == null)
        {
            return false;
        }

        var externalRoutes = _externalTopology.Slots
            .Select(x => (IMessageRoute)new MessageRoute(messageType, x, runtime))
            .ToArray();

        var localRoutes = _localTopology.Slots
            .Select(x => (IMessageRoute)new MessageRoute(messageType, x, runtime))
            .ToArray();

        var externalEndpoints = _externalTopology.Slots.ToArray();

        route = new GlobalPartitionedRoute(
            _externalTopology.Uri,
            runtime.Options.MessagePartitioning,
            externalRoutes,
            localRoutes,
            externalEndpoints);

        return true;
    }
}
