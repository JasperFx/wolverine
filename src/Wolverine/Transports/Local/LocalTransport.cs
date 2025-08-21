using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Routing;
using Wolverine.Util;

namespace Wolverine.Transports.Local;

internal class LocalTransport : TransportBase<LocalQueue>, ILocalMessageRoutingConvention
{
    private readonly List<IDelayedEndpointConfiguration> _delayedConfigurations = new();
    private readonly Cache<string, LocalQueue> _queues;

    private Action<Type, IListenerConfiguration> _customization = (_, _) => { };
    private Func<Type, string> _determineName = t => t.ToMessageTypeName().Replace('+', '.').Replace("[", "").Replace("]", "");


    public LocalTransport() : base(TransportConstants.Local, "Local (In Memory)")
    {
        _queues = new Cache<string, LocalQueue>(name => new LocalQueue(name));

        _queues.FillDefault(TransportConstants.Default);
        _queues.FillDefault(TransportConstants.Replies);

        var scheduledQueue = _queues[TransportConstants.Scheduled];
        scheduledQueue.Mode = EndpointMode.Durable;
        scheduledQueue.Role = EndpointRole.System;

        var durableQueue = _queues[TransportConstants.Durable];
        durableQueue.Mode = EndpointMode.Durable;
        durableQueue.Role = EndpointRole.System;

        var agentQueue = _queues[TransportConstants.Agents];
        agentQueue.TelemetryEnabled = false;
        agentQueue.Subscriptions.Add(new Subscription
            { Scope = RoutingScope.Implements, BaseType = typeof(IAgentCommand) });
        agentQueue.MaxDegreeOfParallelism = 20;
        agentQueue.Role = EndpointRole.System;
        agentQueue.Mode = EndpointMode.BufferedInMemory;
    }

    public Dictionary<Type, LocalQueue> Assignments { get; } = new();

    /// <summary>
    ///     Override the type to local queue naming. By default this is the MessageTypeName
    ///     to lower case invariant
    /// </summary>
    /// <param name="determineName"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public ILocalMessageRoutingConvention Named(Func<Type, string> determineName)
    {
        _determineName = determineName ?? throw new ArgumentNullException(nameof(determineName));
        return this;
    }

    /// <summary>
    ///     Customize the endpoints
    /// </summary>
    /// <param name="customization"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public ILocalMessageRoutingConvention CustomizeQueues(Action<Type, IListenerConfiguration> customization)
    {
        _customization = customization ?? throw new ArgumentNullException(nameof(customization));
        return this;
    }

    public ILocalMessageRoutingConvention IsAdditive(bool additive)
    {
        
        return this;
    }

    protected override IEnumerable<LocalQueue> endpoints()
    {
        return _queues;
    }

    protected override LocalQueue findEndpointByUri(Uri uri)
    {
        var queueName = QueueName(uri);
        var settings = _queues[queueName];

        return settings;
    }

    public override Endpoint ReplyEndpoint()
    {
        return _queues[TransportConstants.Replies];
    }

    public IEnumerable<LocalQueue> AllQueues()
    {
        return _queues;
    }

    /// <summary>
    ///     Retrieves a local queue by name
    /// </summary>
    /// <param name="queueName"></param>
    /// <returns></returns>
    public LocalQueue QueueFor(string queueName)
    {
        return _queues[queueName.ToLowerInvariant()];
    }

    public static string QueueName(Uri uri)
    {
        if (uri == TransportConstants.LocalUri)
        {
            return TransportConstants.Default;
        }

        if (uri == TransportConstants.DurableLocalUri)
        {
            return TransportConstants.Durable;
        }

        if (uri.Scheme == TransportConstants.Local)
        {
            return uri.Host;
        }

        var lastSegment = uri.Segments.Skip(1).LastOrDefault();

        return lastSegment ?? TransportConstants.Default;
    }

    public static Uri AtQueue(Uri uri, string queueName)
    {
        if (queueName.IsEmpty())
        {
            return uri;
        }

        if (uri.Scheme == TransportConstants.Local && uri.Host != TransportConstants.Durable)
        {
            return new Uri("local://" + queueName);
        }

        return new Uri(uri, queueName);
    }

    internal LocalQueue FindQueueForMessageType(Type messageType)
    {
        if (Assignments.TryGetValue(messageType, out var queue))
        {
            return queue;
        }

        return FindOrCreateQueueForMessageTypeByConvention(messageType);
    }

    internal void DiscoverListeners(IWolverineRuntime runtime, IReadOnlyList<Type> handledMessageTypes)
    {
        if (!runtime.Options.LocalRoutingConventionDisabled)
        {
            foreach (var messageType in handledMessageTypes)
            {
                var chain = runtime.Options.HandlerGraph.ChainFor(messageType);
                if (chain.Handlers.Any())
                {
                    FindOrCreateQueueForMessageTypeByConvention(messageType);
                }
            }
        }

        // Apply individual queue configuration
        foreach (var delayedConfiguration in _delayedConfigurations)
        {
            delayedConfiguration.Apply();
        }
    }

    internal LocalQueue FindOrCreateQueueForMessageTypeByConvention(Type messageType)
    {
        var queueName = messageType.GetAttribute<LocalQueueAttribute>()?.QueueName ?? _determineName(messageType);

        if (queueName.IsEmpty())
        {
            return _queues[TransportConstants.Default];
        }

        var queue = AllQueues().FirstOrDefault(x => x.EndpointName == queueName);

        if (queue == null)
        {
            queue = QueueFor(queueName);

            var listener = new ListenerConfiguration(queue);
            _customization(messageType, listener);

            listener.As<IDelayedEndpointConfiguration>().Apply();
        }

        queue.HandledMessageTypes.Add(messageType);

        Assignments[messageType] = queue;

        return queue;
    }

    internal IEnumerable<Endpoint> DiscoverSenders(Type messageType, IWolverineRuntime runtime)
    {
        var chain = runtime.Options.HandlerGraph.ChainFor(messageType);
        
        // This only covers the default message type to local queue routing
        if (Assignments.TryGetValue(messageType, out var queue))
        {
            yield return queue;
        }
        
        // Now do "sticky" assignments too

        if (chain == null)
        {
            yield break;
        }
        
        foreach (var endpoint in chain.ByEndpoint.SelectMany(x => x.Endpoints))
        {
            yield return endpoint;
        }
    }

    internal LocalQueueConfiguration ConfigureQueueFor(Type messageType)
    {
        var configuration = new LocalQueueConfiguration(() => FindQueueForMessageType(messageType));
        _delayedConfigurations.Add(configuration);

        return configuration;
    }

    internal void ApplyConfiguration(HandlerChain chain)
    {
        // Gotta go recursive
        foreach (var handlerChain in chain.ByEndpoint)
        {
            ApplyConfiguration(handlerChain);
        }
        
        var configured = chain.Handlers.Select(x => x.HandlerType)
            .Where(x => x.CanBeCastTo(typeof(IConfigureLocalQueue))).ToArray();

        if (!configured.Any()) return;

        // Is it sticky?
        if (chain.Endpoints.OfType<LocalQueue>().Any())
        {
            foreach (var handlerType in configured)
            {
                var applier = typeof(Applier<>).CloseAndBuildAs<IApplier>(handlerType);
                foreach (var localQueue in chain.Endpoints.OfType<LocalQueue>())
                {
                    applier.Apply(new LocalQueueConfiguration(localQueue));
                }
            }
        }
        else
        {
            var configuration = ConfigureQueueFor(chain.MessageType);
            foreach (var handlerType in configured)
            {
                typeof(Applier<>).CloseAndBuildAs<IApplier>(handlerType).Apply(configuration);
            }
        }
    }

    private interface IApplier
    {
        void Apply(LocalQueueConfiguration configuration);
    }

    private class Applier<T> : IApplier where T : IConfigureLocalQueue
    {
        public void Apply(LocalQueueConfiguration configuration)
        {
            T.Configure(configuration);
        }
    }
        
}