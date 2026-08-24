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
    private EndpointMode _mode = EndpointMode.Durable;
    private bool _nativeAcks;

    public GlobalPartitionedMessageTopology(WolverineOptions options)
    {
        _options = options;
    }

    internal PartitionedMessageTopology? ExternalTopology => _externalTopology;
    internal LocalPartitionedMessageTopology? LocalTopology => _localTopology;

    /// <summary>
    /// GH-3709. True when <see cref="ProcessInParallelWithNativeAcks"/> has been called: the slots settle
    /// their own broker deliveries and there is no companion local topology or bridge at all.
    /// </summary>
    internal bool UsesNativeAcks => _nativeAcks;

    /// <summary>
    /// Opt the partitioned slots — the external endpoints AND their companion local queues — out of
    /// the default <see cref="EndpointMode.Durable"/>. Use <see cref="EndpointMode.BufferedInMemory"/>
    /// for lossy, re-reported traffic (telemetry, metrics) where at-most-once delivery with
    /// sender-side FIFO ordering is sufficient and store-and-forwarding every envelope through the
    /// application's message store is the wrong trade (GH-3882). The default remains Durable.
    /// Order-independent: may be called before or after the external topology is configured.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="EndpointMode.Inline"/> is not valid here — partitioned slots depend on queueing
    /// (the external listener bridges into the companion local queue) that inline endpoints bypass.
    /// </exception>
    public GlobalPartitionedMessageTopology Mode(EndpointMode mode)
    {
        if (mode == EndpointMode.NativeAck)
        {
            // GH-3708. A global partitioned topology bridges its external listener into a companion LOCAL queue
            // (GlobalPartitionedReceiverBridge), and a local queue has no broker delivery to settle -- so the
            // native ack would have nothing to ack against. Endpoint-level PartitionProcessingByGroupId() on a
            // NativeAck listener is the supported shape for partitioned native-ack processing.
            throw new ArgumentOutOfRangeException(nameof(mode),
                $"{nameof(EndpointMode)}.{nameof(EndpointMode.NativeAck)} cannot be set through {nameof(Mode)}() on a global partitioned topology. "
                + "The default topology bridges each slot into a companion local queue, which has no broker delivery to settle natively. "
                + $"Call {nameof(ProcessInParallelWithNativeAcks)}() instead -- it removes the companion local topology and the bridge so the "
                + "slot listeners settle their own deliveries. PartitionProcessingByGroupId() directly on a native-ack listener is the "
                + "endpoint-level equivalent.");
        }

        if (mode == EndpointMode.Inline)
        {
            throw new ArgumentOutOfRangeException(nameof(mode),
                "EndpointMode.Inline is not supported for global partitioned topologies. Partitioned slots depend on queueing (the external listener bridges into the companion local queue), which inline endpoints bypass. Use Durable (default) or BufferedInMemory.");
        }

        _mode = mode;
        applyMode();
        return this;
    }

    /// <summary>
    /// Process this topology's slots in parallel with native broker acknowledgements instead of the
    /// durable inbox: each slot listener settles its own deliveries when the handler completes, and
    /// shards into sequential lanes by group id in memory. Partitioned clustering with no database at
    /// all — one exclusive consumer per slot across the cluster, no two messages of a group running
    /// concurrently, at-least-once delivery owned by the broker rather than by the inbox.
    /// </summary>
    /// <remarks>
    /// <para>GH-3709. This is what makes <see cref="EndpointMode.NativeAck"/> reachable from a global
    /// partitioned topology. The default topology bridges each external listener into a companion local
    /// queue and does the partitioned execution there (<c>GlobalPartitionedReceiverBridge</c>), which is
    /// exactly why <see cref="Mode"/> refuses NativeAck — a local queue has no broker delivery to settle.
    /// Calling this removes the companion topology and the bridge, so the slot's own receiver shards
    /// directly and the ack stays tied to the delivery's own channel.</para>
    ///
    /// <para>Trade-offs against the Durable default: no inbox insert or mark-handled per message and no
    /// database on the path, but also no inbox dedup, no outbox atomicity with handler side effects, and
    /// recovery is the broker's redelivery rather than inbox recovery. Ordering is per-slot best effort;
    /// two groups hashing to the same slot serialize against each other.</para>
    ///
    /// <para>The transport must opt in to <see cref="EndpointMode.NativeAck"/>. If it has not, applying
    /// the mode throws at bootstrap naming the endpoint type — see <c>Endpoint.supportsNativeAck</c>.</para>
    /// </remarks>
    public GlobalPartitionedMessageTopology ProcessInParallelWithNativeAcks()
    {
        _nativeAcks = true;
        _mode = EndpointMode.NativeAck;

        // A companion local topology may already exist if LocalQueues() ran first. It is meaningless
        // here -- drop it rather than leaving queues nothing routes to.
        _localTopology = null;

        applyMode();
        return this;
    }

    private void applyMode()
    {
        if (_externalTopology != null)
        {
            foreach (var slot in _externalTopology.Slots)
            {
                slot.Mode = _mode;
            }
        }

        if (_localTopology != null)
        {
            foreach (var slot in _localTopology.Slots)
            {
                slot.Mode = _mode;
            }
        }
    }

    public void LocalQueues(string baseQueueName, int numberOfEndpoints)
    {
        if (_nativeAcks)
        {
            throw new InvalidOperationException(
                $"A native-ack global partitioned topology has no companion local queues -- {nameof(ProcessInParallelWithNativeAcks)}() "
                + $"makes each slot settle its own broker deliveries, so there is nothing for {nameof(LocalQueues)}() to configure. "
                + $"Remove one of the two calls.");
        }

        _localTopology = new LocalPartitionedMessageTopology(_options, baseQueueName, numberOfEndpoints);
        applyMode();
    }

    internal void SetExternalTopology(Func<WolverineOptions, PartitionedMessageTopology> factory, string baseName)
    {
        SetExternalTopology(factory(_options), baseName);
    }

    internal void SetExternalTopology(PartitionedMessageTopology topology, string baseName)
    {
        _externalTopology = topology;

        // GH-3709. A native-ack topology has no companion local topology and no bridge: the slot's own
        // receiver shards by group id so the ack stays tied to the delivery's own channel.
        if (_localTopology == null && !_nativeAcks)
        {
            // Create companion local topology with matching slot count
            var localBaseName = $"global-{baseName}";
            _localTopology = new LocalPartitionedMessageTopology(_options, localBaseName, topology.Slots.Count);
        }

        // Stamp the topology's mode — Durable unless the user opted the slots into
        // BufferedInMemory via Mode() (GH-3882) — on every external endpoint and companion local
        // queue. Runs after the user's configure callback, which is why the opt-out lives on this
        // class rather than on the individual slots: a per-slot Mode set in the callback would be
        // overwritten here.
        applyMode();

        // Tag each external slot endpoint with its companion local queue URI. ListeningAgent wires the
        // GlobalPartitionedReceiverBridge off exactly this property, so leaving it null on a native-ack
        // topology is what keeps the bridge out of the picture -- there is no separate opt-out there.
        // Only tag if slot counts match; mismatches will be caught by AssertValidity()
        if (_localTopology != null && topology.Slots.Count == _localTopology.Slots.Count)
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

        // GH-3709. A native-ack topology deliberately has no local topology, so the two rules below --
        // both of which exist to keep the companion queues lined up with the bridge -- do not apply.
        // The subscription and external-topology rules above still do.
        if (_nativeAcks)
        {
            return;
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

        if (_externalTopology == null)
        {
            return false;
        }

        // A native-ack topology has no local slots at all; every other topology needs them for the
        // local shortcut and is not routable until they exist.
        if (_localTopology == null && !_nativeAcks)
        {
            return false;
        }

        var externalRoutes = _externalTopology.Slots
            .Select(x => (IMessageRoute)MessageRoute.For(messageType, x, runtime))
            .ToArray();

        var localRoutes = _localTopology?.Slots
            .Select(x => (IMessageRoute)MessageRoute.For(messageType, x, runtime))
            .ToArray() ?? [];

        var externalEndpoints = _externalTopology.Slots.ToArray();

        route = new GlobalPartitionedRoute(
            _externalTopology.Uri,
            runtime.Options.MessagePartitioning,
            externalRoutes,
            localRoutes,
            externalEndpoints,
            _nativeAcks);

        return true;
    }
}
