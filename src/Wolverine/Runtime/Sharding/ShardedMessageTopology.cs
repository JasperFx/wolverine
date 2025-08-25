using System.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime.Routing;

namespace Wolverine.Runtime.Sharding;

public abstract class ShardedMessageTopology<TListener, TSubscriber> : ShardedMessageTopology
    where TListener : IListenerConfiguration<TListener>
    where TSubscriber : ISubscriberConfiguration<TSubscriber>
{
    private ShardSlots _listeningSlots;
    
    public ShardedMessageTopology(WolverineOptions options, ShardSlots? listeningSlots, string baseName, int numberOfEndpoints) : base(options, listeningSlots, baseName, numberOfEndpoints)
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
    public ShardSlots MaxDegreeOfParallelism
    {
        get => _listeningSlots;
        set
        {
            _listeningSlots = value;
            ConfigureListening(x => x.ShardListeningByGroupId(value));
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
        foreach (var name in _names)
        {
            var listener = buildListener(_options, name);
            configure(listener);
        }
    }
} 

public abstract class ShardedMessageTopology
{
    protected readonly WolverineOptions _options;
    
    protected ShardedMessageTopology(WolverineOptions options, ShardSlots? listeningSlots, string baseName, int numberOfEndpoints)
    {
        if (numberOfEndpoints <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numberOfEndpoints), "Must be a positive number");
        }
        
        _options = options;
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

    internal void AssertValidity()
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
            var innerRoutes = _slots.Select(x => new MessageRoute(messageType, x, runtime)).ToArray();
            
            // TODO -- do we let you configure grouping here too????
            route = new ShardedMessageRoute(Uri, runtime.Options.MessageGrouping, innerRoutes);
            return true;
        }

        return false;
    }

    internal bool Matches(Type messageType)
    {
        return _subscriptions.Any(x => x.Matches(messageType));
    }

    public IReadOnlyList<Endpoint> Slots => _slots;

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

    internal Endpoint SelectSlot(Envelope contextEnvelope)
    {
        if (contextEnvelope == null) throw new ArgumentNullException(nameof(contextEnvelope));
        
        var slot = contextEnvelope.SlotForSending(_slots.Count, _options.MessageGrouping);
        return _slots[slot];
    }
}