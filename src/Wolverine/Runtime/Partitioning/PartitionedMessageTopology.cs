using System.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime.Routing;

namespace Wolverine.Runtime.Partitioning;

public abstract class PartitionedMessageTopology<TListener, TSubscriber> : PartitionedMessageTopology
    where TListener : IListenerConfiguration<TListener>
    where TSubscriber : ISubscriberConfiguration<TSubscriber>
{
    private PartitionSlots _listeningSlots;

    /// <summary>
    /// When true, this topology only publishes to its sharded endpoints and never registers a listener
    /// for them on this node. <see cref="ConfigureListening" /> and <see cref="MaxDegreeOfParallelism" />
    /// then have no listening effect. Set it in a derived constructor.
    /// </summary>
    protected bool PublishOnly { get; set; }

    internal override bool ListensLocally => !PublishOnly;

    public PartitionedMessageTopology(WolverineOptions options, PartitionSlots? listeningSlots, string baseName, int numberOfEndpoints) : base(options, listeningSlots, baseName, numberOfEndpoints)
    {
        if (listeningSlots.HasValue)
        {
            MaxDegreeOfParallelism = listeningSlots.Value;
        }
    }

    /// <summary>
    /// Build this topology's endpoints against a named broker rather than the default,
    /// unnamed transport.
    /// </summary>
    public PartitionedMessageTopology(WolverineOptions options, PartitionSlots? listeningSlots, string baseName, int numberOfEndpoints, BrokerName? brokerName) : base(options, listeningSlots, baseName, numberOfEndpoints, brokerName)
    {
        if (listeningSlots.HasValue)
        {
            MaxDegreeOfParallelism = listeningSlots.Value;
        }
    }

    protected abstract TListener buildListener(WolverineOptions options, string name);

    protected abstract TSubscriber buildSubscriber(IPublishToExpression expression, string name);

    /// <summary>
    /// Override the maximum number of parallel messages that can be executed
    /// at one time in one of the sharded local queues. Default is 5.
    /// </summary>
    public PartitionSlots MaxDegreeOfParallelism
    {
        get => _listeningSlots;
        set
        {
            _listeningSlots = value;
            ConfigureListening(x => x.PartitionProcessingByGroupId(value));
        }
    }
    
    public void ConfigureSender(Action<TSubscriber> configure)
    {
        foreach (var name in _names)
        {
            var expression = new PublishingExpression(_options);
            var subscriber = buildSubscriber(expression, name);
            configure(subscriber);
        }
    }
    
    public void ConfigureListening(Action<TListener> configure)
    {
        if (PublishOnly) return;

        foreach (var name in _names)
        {
            var listener = buildListener(_options, name);
            configure(listener);
        }
    }
} 

public abstract class PartitionedMessageTopology : IPartitionedTopology
{
    protected readonly WolverineOptions _options;

    /// <summary>
    /// The named broker this topology's endpoints should be built against, or null for the
    /// default/unnamed transport. Assigned before <see cref="buildEndpoint" /> is first called,
    /// which matters because buildEndpoint runs from this constructor -- ahead of any derived
    /// class's own constructor body.
    /// </summary>
    protected readonly BrokerName? _brokerName;

    protected PartitionedMessageTopology(WolverineOptions options, PartitionSlots? listeningSlots, string baseName, int numberOfEndpoints)
        : this(options, listeningSlots, baseName, numberOfEndpoints, null)
    {
    }

    /// <summary>
    /// Build this topology's endpoints against a named broker rather than the default,
    /// unnamed transport.
    /// </summary>
    protected PartitionedMessageTopology(WolverineOptions options, PartitionSlots? listeningSlots, string baseName, int numberOfEndpoints, BrokerName? brokerName)
    {
        if (numberOfEndpoints <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numberOfEndpoints), "Must be a positive number");
        }

        _options = options;
        _brokerName = brokerName;
        _names = new string[numberOfEndpoints];

        for (int i = 0; i < numberOfEndpoints; i++)
        {
            var name = $"{baseName}{i + 1}";
            _names[i] = name;

            var endpoint = buildEndpoint(options, name);
            endpoint.UsedInShardedTopology = true;
            endpoint.ListenerScope = ListenerScope.Exclusive;
            endpoint.GroupShardingSlotNumber = listeningSlots;
            
            _slots.Add(endpoint);
        }

        var transportScheme = _slots[0].Uri.Scheme;
        Uri = new Uri($"shard://{transportScheme}/{baseName}");
    }

    public void AssertValidity()
    {
        if (!_subscriptions.Any())
        {
            throw new InvalidOperationException("At least one message type matching policy is required");
        }
    }

    protected abstract Endpoint buildEndpoint(WolverineOptions options, string name);

    private readonly List<Subscription> _subscriptions = new();
    internal bool AutoAddSubscriptions { get; private set; }
    private readonly List<Endpoint> _slots = new();
    protected readonly string[] _names;
    
    public Uri Uri { get; }

    internal bool TryMatch(Type messageType, IWolverineRuntime runtime, out IMessageRoute? route)
    {
        route = default!;

        if (Matches(messageType))
        {
            var innerRoutes = _slots.Select(x => MessageRoute.For(messageType, x, runtime)).ToArray();
            
            route = new ShardedMessageRoute(Uri, runtime.Options.MessagePartitioning, innerRoutes);
            
            return true;
        }

        return false;
    }

    internal bool Matches(Type messageType)
    {
        return _subscriptions.Any(x => x.Matches(messageType));
    }

    public IReadOnlyList<Endpoint> Slots => _slots;

    private readonly List<Type> _handlerTypes = new();

    /// <summary>
    ///     Declare that this topology runs the handlers in <typeparamref name="THandler"/>. This only matters for a
    ///     message type that more than one partitioned topology publishes, as in a modular monolith where each
    ///     module has its own topology: each of its handler types runs only on the topology that declares it.
    ///     A message type published through just one topology runs all of its handlers there, declared or not.
    /// </summary>
    /// <typeparam name="THandler">The handler type</typeparam>
    public void HandledBy<THandler>()
    {
        HandledBy(typeof(THandler));
    }

    /// <summary>
    ///     Declare that this topology runs the handlers in these handler types. See <see cref="HandledBy{THandler}"/>
    /// </summary>
    public void HandledBy(params Type[] handlerTypes)
    {
        ArgumentNullException.ThrowIfNull(handlerTypes);
        foreach (var handlerType in handlerTypes)
        {
            ArgumentNullException.ThrowIfNull(handlerType);
            if (!_handlerTypes.Contains(handlerType)) _handlerTypes.Add(handlerType);
        }
    }

    /// <summary>
    ///     False for a topology that only publishes to its endpoints and never listens to them in this application
    /// </summary>
    internal virtual bool ListensLocally => true;

    string IPartitionedTopology.Name => Uri.ToString();
    bool IPartitionedTopology.ListensLocally => ListensLocally;
    IReadOnlyList<Type> IPartitionedTopology.HandlerTypes => _handlerTypes;
    bool IPartitionedTopology.Matches(Type messageType) => Matches(messageType);
    bool IPartitionedTopology.ExecutesAt(Endpoint endpoint) => _slots.Contains(endpoint);

    bool IPartitionedTopology.TryMatch(Type messageType, IWolverineRuntime runtime, out IMessageRoute? route) =>
        TryMatch(messageType, runtime, out route);

    /// <summary>
    ///     Create a publishing rule for a single message type
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public void Message<T>()
    {
        Message(typeof(T));
    }
    
    
    /// <summary>
    ///     Create a publishing rule for a single message type
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    public void Message(Type type)
    {
        _subscriptions.Add(Subscription.ForType(type));
    }
    
    
    /// <summary>
    ///     Create a publishing rule for all message types from within the
    ///     specified namespace
    /// </summary>
    /// <param name="namespace"></param>
    /// <returns></returns>
    public void MessagesFromNamespace(string @namespace)
    {
        AutoAddSubscriptions = true;
        
        _subscriptions.Add(new Subscription
        {
            Match = @namespace,
            Scope = RoutingScope.Namespace
        });
    }


    /// <summary>
    ///     Create a publishing rule for all message types from within the
    ///     specified namespace holding the marker type "T"
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public void MessagesFromNamespaceContaining<T>()
    {
        MessagesFromNamespace(typeof(T).Namespace!);
    }
    
    
    /// <summary>
    ///     Create a publishing rule for all messages from the given assembly
    /// </summary>
    /// <param name="assembly"></param>
    /// <returns></returns>
    public void MessagesFromAssembly(Assembly assembly)
    {
        _subscriptions.Add(new Subscription(assembly));
        AutoAddSubscriptions = true;
    }

    /// <summary>
    ///     Create a publishing rule for all messages from the given assembly that contains the type T
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public void MessagesFromAssemblyContaining<T>()
    {
        MessagesFromAssembly(typeof(T).Assembly);
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

    internal List<IGroupingRule> GroupingRules { get; } = new();

    /// <summary>
    ///     Use the Envelope.TenantId as the GroupId of the message types published through this topology.
    ///     Unlike <see cref="MessagePartitioningRules.ByTenantId" />, this reaches no other message type.
    ///     A topology's own rules are consulted before the application-wide rules, and the application-wide
    ///     rules still apply to any of its messages that none of them match.
    /// </summary>
    public void GroupByTenantId()
    {
        GroupBy(new TenantGroupingRule());
    }

    /// <summary>
    ///     Determine the GroupId of the messages published through this topology that can be cast to
    ///     "T". This rule is consulted before the application-wide rules, which still apply to any of this
    ///     topology's messages it does not match.
    /// </summary>
    public void GroupBy<T>(Func<T, string> strategy)
    {
        GroupBy(new MessageGrouping<T>(strategy));
    }

    /// <summary>
    ///     Determine the GroupId of the messages published through this topology with a custom rule.
    ///     Rules on one topology are evaluated in the order they are declared, ahead of the application-wide
    ///     rules, which still apply to any of this topology's messages none of them match.
    /// </summary>
    public void GroupBy(IGroupingRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        GroupingRules.Add(rule);
        _options.MessagePartitioning.TopologyGroupingChanged();
    }

    internal Endpoint SelectSlot(Envelope contextEnvelope)
    {
        if (contextEnvelope == null) throw new ArgumentNullException(nameof(contextEnvelope));
        
        var slot = contextEnvelope.SlotForSending(_slots.Count, _options.MessagePartitioning);
        return _slots[slot];
    }
}