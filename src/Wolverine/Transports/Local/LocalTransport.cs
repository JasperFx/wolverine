using System.Diagnostics.CodeAnalysis;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
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


    public LocalTransport() : base(TransportConstants.Local, "Local (In Memory)", ["local"])
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
    
    public override bool TryBuildBrokerUsage(out BrokerDescription description)
    {
        description = default!;
        return false;
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
                // GH-5136. The message type's own queue is for a chain that is not bound to an endpoint of
                // its own: the only chain of a message type with one handler type that is not sticky
                var group = runtime.Options.HandlerGraph.GroupFor(messageType);
                if (group != null && hasUnboundChain(group))
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

        warnAboutMessageTypeQueuesNothingRoutesTo(runtime);
    }

    private static bool hasUnboundChain(MessageHandlerGroup group) => group.UnboundChains().Any(x => x.Handlers.Any());

    // GH-5137. LocalQueueFor<T>() or [LocalQueue] on a message type with several handler types configures a queue
    // that only drains envelopes persisted before the handler types were split: each handler type runs on a
    // queue of its own
    private void warnAboutMessageTypeQueuesNothingRoutesTo(IWolverineRuntime runtime)
    {
        foreach (var (messageType, queue) in Assignments)
        {
            var group = runtime.Options.HandlerGraph.GroupFor(messageType);
            if (group == null || group.Chains.Count == 0 || hasUnboundChain(group)) continue;

            runtime.Logger.LogWarning(
                "Local queue {Queue} is configured for message type {MessageType}, but that message type is handled by {HandlerTypes}, each on a local queue of its own, so nothing routes to {Queue}. Configure the handler types' queues instead",
                queue.Uri, messageType.FullNameInCode(), group.DescribeBindings(), queue.Uri);
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
        var group = runtime.Options.HandlerGraph.GroupFor(messageType);
        
        // This only covers the default message type to local queue routing. GH-5137: when every chain of the
        // message type is bound to a queue of its own, the message type's queue -- created anyway by
        // LocalQueueFor<T>() or [LocalQueue] -- only drains what was persisted to it before the handler types
        // were split. Routing there too would relay a second copy to every handler type
        if (Assignments.TryGetValue(messageType, out var queue) && (group == null || hasUnboundChain(group)))
        {
            yield return queue;
        }
        
        // Now do "sticky" assignments too

        if (group == null)
        {
            yield break;
        }
        
        foreach (var endpoint in group.BoundChains().SelectMany(x => x.Endpoints))
        {
            // GH-4510. A sticky handler binding says "deliver this message type to this listener". It is NOT a
            // claim that anything can send to the endpoint, and conflating the two crashed host startup: an
            // Azure Service Bus subscription is receive-only -- you publish to its topic -- so building a
            // sending agent for it threw NotSupportedException out of PrepopulateRoutingCache, which runs for
            // every discovered message type during StartAsync. On 5.x routes were built lazily on first send,
            // so a message type that was only ever received never hit this path at all.
            if (!endpoint.CanSend) continue;

            yield return endpoint;
        }
    }

    internal LocalQueueConfiguration ConfigureQueueFor(Type messageType)
    {
        var configuration = new LocalQueueConfiguration(() => FindQueueForMessageType(messageType));
        _delayedConfigurations.Add(configuration);

        return configuration;
    }

    // CloseAndBuildAs<IApplier>(handlerType) closes the internal Applier<>
    // pseudo-generic over a runtime-resolved IConfigureLocalQueue handler type.
    // Apps in TypeLoadMode.Static have their handler graph pre-discovered into
    // source-generated registration code (see the AOT publishing guide); the
    // handler types they reference are statically rooted and CloseAndBuildAs's
    // reflection finds them safely. Trim-only apps without source generation
    // need handler types preserved via TrimmerRootDescriptor or
    // [DynamicallyAccessedMembers] on the surface that produces the handler
    // type list.
    //
    // Leaf suppression rather than annotating ApplyConfiguration with
    // [RequiresDynamicCode] because LocalTransport.ApplyConfiguration is on
    // the bootstrap-time HandlerGraph.Compile cascade — propagating [Requires*]
    // up that cascade would force every Wolverine startup path to acknowledge
    // the warning. The AOT-clean Static-mode path doesn't add new runtime
    // codegen at startup; it just runs the source-generated registrations.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Runtime-resolved handler types; AOT-clean apps in TypeLoadMode.Static have these baked into source-generated registration. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Runtime-resolved handler types; AOT-clean apps in TypeLoadMode.Static have these baked into source-generated registration. See AOT guide.")]
    internal void ApplyConfiguration(HandlerChain chain)
    {
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