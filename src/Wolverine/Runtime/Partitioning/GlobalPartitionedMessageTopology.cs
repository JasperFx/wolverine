using System.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime.Routing;
using Wolverine.Util;

namespace Wolverine.Runtime.Partitioning;

public class GlobalPartitionedMessageTopology
{
    private readonly WolverineOptions _options;
    private readonly List<Subscription> _subscriptions = new();
    private readonly List<Type> _exclusions = new();
    private readonly HashSet<string> _messageTypeNames = new(StringComparer.OrdinalIgnoreCase);
    private PartitionedMessageTopology? _externalTopology;
    private LocalPartitionedMessageTopology? _localTopology;

    public GlobalPartitionedMessageTopology(WolverineOptions options)
    {
        _options = options;
    }

    internal PartitionedMessageTopology? ExternalTopology => _externalTopology;
    internal LocalPartitionedMessageTopology? LocalTopology => _localTopology;

    public void LocalQueues(string baseQueueName, int numberOfEndpoints)
    {
        _localTopology = new LocalPartitionedMessageTopology(_options, baseQueueName, numberOfEndpoints);
    }

    internal void SetExternalTopology(Func<WolverineOptions, PartitionedMessageTopology> factory, string baseName)
    {
        SetExternalTopology(factory(_options), baseName);
    }

    internal void SetExternalTopology(PartitionedMessageTopology topology, string baseName)
    {
        _externalTopology = topology;

        if (_localTopology == null)
        {
            // Create companion local topology with matching slot count
            var localBaseName = $"global-{baseName}";
            _localTopology = new LocalPartitionedMessageTopology(_options, localBaseName, topology.Slots.Count);
        }

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
        // Only tag if slot counts match; mismatches will be caught by AssertValidity()
        if (topology.Slots.Count == _localTopology.Slots.Count)
        {
            for (var i = 0; i < topology.Slots.Count; i++)
            {
                topology.Slots[i].GlobalPartitionLocalQueueUri = _localTopology.Slots[i].Uri;
            }
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

        // Exclusions win regardless of declaration order, so don't seed the name cache that
        // MatchesByMessageTypeName reads (the pre-deserialization path) for an excluded type.
        // Except() performs the mirror-image removal for the other ordering.
        if (!_exclusions.Any(x => x.IsAssignableFrom(type)))
        {
            _messageTypeNames.Add(type.ToMessageTypeName());
        }
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

        if (_localTopology == null)
        {
            throw new InvalidOperationException(
                "A local queue topology must be configured for global partitioning");
        }

        if (_externalTopology.Slots.Count != _localTopology.Slots.Count)
        {
            throw new InvalidOperationException(
                $"The external topology has {_externalTopology.Slots.Count} slots but the local topology has {_localTopology.Slots.Count} slots. These must match for global partitioning.");
        }
    }

    /// <summary>
    /// Exclude a message type — or everything assignable to <typeparamref name="T"/>, so an
    /// interface or base class excludes its whole family — from this topology, even when a
    /// broader rule such as <see cref="MessagesImplementing{T}"/> would otherwise match it.
    /// Exclusions always win.
    /// </summary>
    /// <remarks>
    /// <para>The case this exists for: a message type that legitimately belongs to the topology on
    /// the way IN, but that the receiving application also re-publishes on its way somewhere else.
    /// Because both sides configure their own topology, excluding it on the <em>receiving</em> side
    /// keeps inbound partitioning intact while stopping that application's own re-publish from
    /// re-entering the topology and coming straight back to the handler that published it — an
    /// infinite loop that is invisible in configuration and shows up only as amplified load.</para>
    ///
    /// <para>Excluding a type does not stop this application <em>listening</em> for it on the
    /// topology's slots: the companion-queue bridge is wired per endpoint, not per message type. It
    /// only removes the type from this topology's publishing rules.</para>
    /// </remarks>
    public void Except<T>()
    {
        Except(typeof(T));
    }

    /// <summary>
    /// Exclude a message type — or everything assignable to <paramref name="type"/> — from this
    /// topology. See <see cref="Except{T}"/>.
    /// </summary>
    public void Except(Type type)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));

        _exclusions.Add(type);
        _messageTypeNames.Remove(type.ToMessageTypeName());
    }

    internal bool Matches(Type messageType)
    {
        // Exclusions are checked first and win outright, so an Except<T>() cannot be defeated by
        // the order in which rules were declared.
        if (_exclusions.Any(x => x.IsAssignableFrom(messageType)))
        {
            return false;
        }

        return _subscriptions.Any(x => x.Matches(messageType));
    }

    /// <summary>
    /// Check if a message type name (from envelope metadata) matches this topology's subscriptions.
    /// This is used by the interceptor when the message hasn't been deserialized yet (e.g. Kafka).
    /// </summary>
    internal bool MatchesByMessageTypeName(string? messageTypeName)
    {
        return messageTypeName != null && _messageTypeNames.Contains(messageTypeName);
    }

    /// <summary>
    /// Pre-compute message type names for subscription scopes that can't be resolved from
    /// a string alone (e.g. MessagesImplementing, namespace, assembly).
    /// Called during startup with the set of known handler message types.
    /// </summary>
    internal void ResolveMessageTypeNames(IEnumerable<Type> knownMessageTypes)
    {
        foreach (var type in knownMessageTypes)
        {
            if (Matches(type))
            {
                _messageTypeNames.Add(type.ToMessageTypeName());
            }
        }
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
            .Select(x => (IMessageRoute)MessageRoute.For(messageType, x, runtime))
            .ToArray();

        var localRoutes = _localTopology.Slots
            .Select(x => (IMessageRoute)MessageRoute.For(messageType, x, runtime))
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
