using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using ImTools;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Logging.Debug;
using Microsoft.Extensions.Options;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Runtime.Scheduled;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Util;

namespace Wolverine.Runtime.Handlers;

public partial class HandlerGraph : ICodeFileCollectionWithServices, IWithFailurePolicies
{
    private readonly List<HandlerCall> _calls = new();
    private readonly object _compilingLock = new();

    private readonly IList<Action> _configurations = new List<Action>();

    private readonly object _groupingLock = new();

    internal readonly HandlerDiscovery Discovery = new();

    // GH-5136. One group per message type, holding one chain per handler type
    private ImHashMap<Type, MessageHandlerGroup> _chains = ImHashMap<Type, MessageHandlerGroup>.Empty;

    private ImHashMap<Type, IMessageHandler?> _handlers = ImHashMap<Type, IMessageHandler?>.Empty;

    // GH-5137. The relay of a message type with several chains from an endpoint none of them is bound to. Kept
    // apart from _handlers, which HandlerFor(Type) reads first: a fan-out handler cached there made
    // HandlerFor(Type) answer with the relay, or throw, depending on whether a message had already arrived
    // somewhere from outside
    private ImHashMap<Type, IMessageHandler> _fanoutHandlers = ImHashMap<Type, IMessageHandler>.Empty;

    private bool _hasCompiled;

    private bool _hasGrouped;

    private object _messageTypesLock = new();

    private ImHashMap<string, Type> _messageTypes = ImHashMap<string, Type>.Empty;

    private ImmutableList<Type> _replyTypes = ImmutableList<Type>.Empty;

    public HandlerGraph()
    {
        // All of this is to seed the handler and its associated retry policies
        // for scheduling outgoing messages
        AddMessageHandler(typeof(Envelope), new ScheduledSendEnvelopeHandler(this));

        _messageTypes = _messageTypes.AddOrUpdate(TransportConstants.ScheduledEnvelope, typeof(Envelope));

        RegisterMessageType(typeof(Acknowledgement));
        RegisterMessageType(typeof(FailureAcknowledgement));
    }

    public Dictionary<Type, Type> MappedGenericMessageTypes { get; } = new();

    internal IServiceContainer Container { get; set; } = null!;

    /// <summary>
    /// Every handler chain in the graph. GH-5136: a message type handled by more than one handler type has
    /// one chain per handler type, so a message type can appear here more than once
    /// </summary>
    public HandlerChain[] Chains => _chains.Enumerate().SelectMany(x => x.Value.Chains).ToArray();

    /// <summary>
    /// GH-5136. The handler chains grouped by message type
    /// </summary>
    internal MessageHandlerGroup[] Groups => _chains.Enumerate().Select(x => x.Value).ToArray();

    /// <summary>
    /// GH-5136. The group of handler chains for exactly this message type, or null
    /// </summary>
    internal MessageHandlerGroup? GroupFor(Type messageType)
    {
        return _chains.TryFind(messageType, out var group) ? group : null;
    }

    /// <summary>
    /// GH-5136. The group for this message type, or else the one group whose message type it can be cast to
    /// (the Event&lt;T&gt; to IEvent&lt;T&gt; case)
    /// </summary>
    private MessageHandlerGroup? groupOrCandidateFor(Type messageType)
    {
        if (_chains.TryFind(messageType, out var group)) return group;

        var candidates = _chains.Enumerate().Where(x => messageType.CanBeCastTo(x.Key)).ToArray();
        return candidates.Length == 1 ? candidates[0].Value : null;
    }

    public IEnumerable<Assembly> ExtensionAssemblies => Discovery.Assemblies;
    public List<Assembly> InteropAssemblies { get; } = new();

    public FailureRuleCollection Failures { get; set; } = new();

    public MultipleHandlerBehavior MultipleHandlerBehavior { get; set; } =
        MultipleHandlerBehavior.ClassicCombineIntoOneLogicalHandler;

    public void ConfigureHandlerForMessage<T>(Action<HandlerChain> configure)
    {
        ConfigureHandlerForMessage(typeof(T), configure);
    }

    /// <summary>
    /// Customize the handler chain(s) for a message type after discovery and policies have run. When the
    /// message type has more than one chain -- one per handler type -- the configuration is applied to every
    /// one of them
    /// </summary>
    public void ConfigureHandlerForMessage(Type messageType, Action<HandlerChain> configure)
    {
        _configurations.Add(() =>
        {
            var group = GroupFor(messageType);
            if (group == null) return;

            foreach (var chain in group.Chains)
            {
                configure(chain);
            }
        });
    }

    internal void AddMessageHandler(Type messageType, IMessageHandler handler)
    {
        // Makes error handling and other configuration work cleanly
        if (handler is MessageHandler h and not AgentCommandHandler)
        {
            var chain = new HandlerChain(messageType, this);
            h.Chain = chain;
        }
        
        _handlers = _handlers.AddOrUpdate(messageType, handler);
        RegisterMessageType(messageType);
    }

    // Called from Endpoint.Compile, so a manual handler registered on one endpoint gets its chain and its
    // message type up front. Deliberately not done by enumerating every transport's endpoints from
    // HandlerGraph.Compile: some transports materialize endpoints as a side effect of being enumerated (the
    // Amazon SQS default dead letter queue, for one) before their listeners' configuration has been applied.
    internal void RegisterStickyMessageHandlers(Endpoint endpoint)
    {
        foreach (var pair in endpoint.AllStickyMessageHandlers())
        {
            ensureStickyMessageHandlerChain(pair.Key, pair.Value);
            RegisterMessageType(pair.Key);
        }
    }

    // The same chain wiring AddMessageHandler does, for a handler registered on one endpoint. Normally done
    // from Endpoint.Compile; HandlerFor(Type, Endpoint) repeats it in case the lookup gets there first.
    private void ensureStickyMessageHandlerChain(Type messageType, IMessageHandler handler)
    {
        if (handler is not MessageHandler { Chain: null } messageHandler || handler is AgentCommandHandler) return;

        lock (_compilingLock)
        {
            if (messageHandler.Chain != null) return;

            var chain = new HandlerChain(messageType, this);
            messageHandler.ApplyChainConfiguration(chain);
            messageHandler.Chain = chain;
        }
    }

    private void assertNotGrouped()
    {
        if (_hasGrouped)
        {
            // GH-4532: the cause is almost always configuration attempted after the host is up.
            throw new InvalidOperationException(
                "This HandlerGraph has already been grouped/compiled. Configure opts.Discovery inside UseWolverine(), not after the host has started.");
        }
    }

    public void AddRange(IEnumerable<HandlerCall> calls)
    {
        assertNotGrouped();
        _calls.AddRange(calls);
    }

    public IMessageHandler? HandlerFor<T>()
    {
        return HandlerFor(typeof(T));
    }

    /// <summary>
    /// The handler chain for this message type, or null if it has none. GH-5136: a message type handled by
    /// more than one handler type has one chain per handler type, and asking for "the" chain of it throws
    /// <see cref="AmbiguousHandlerChainException"/>
    /// </summary>
    public HandlerChain? ChainFor(Type messageType)
    {
        if (!_chains.TryFind(messageType, out var group)) return null;

        return group.Chains.Count switch
        {
            0 => null,
            1 => group.Chains[0],
            _ => throw new AmbiguousHandlerChainException(messageType, group.Chains)
        };
    }

    /// <summary>
    ///     The chain that owns the handling of <paramref name="messageType" />: the discovered chain if there is
    ///     one, otherwise the chain of a pre-canned <see cref="MessageHandler" /> registered with
    ///     <see cref="WolverineOptions.AddMessageHandler(Type, IMessageHandler)" />. Returns null when neither
    ///     exists.
    /// </summary>
    /// <remarks>
    ///     <see cref="ChainFor(Type)" /> reads the discovered chains only. That is right for anything that needs
    ///     generated code, and wrong for anything that only needs the chain's runtime settings -- batching, for
    ///     one, which needs the batch type's chain for its failure rules and its envelope identity, and would
    ///     otherwise refuse a batch handler that is perfectly able to run. Reads the pre-canned map directly
    ///     rather than through <see cref="HandlerFor(Type)" />, so a miss is not memoized and an
    ///     <see cref="IAgentCommand" /> type does not take the agent path.
    /// </remarks>
    internal HandlerChain? ChainOrRegisteredHandlerChainFor(Type messageType)
    {
        // GH-5136. Several batch handlers of one batch type are several chains; any of them carries the
        // runtime settings this is asked for
        return GroupFor(messageType)?.Chains.FirstOrDefault()
               ?? (_handlers.TryFind(messageType, out var handler) && handler is MessageHandler { Chain: { } chain }
                   ? chain
                   : null);
    }

    public HandlerChain? ChainFor<T>()
    {
        return ChainFor(typeof(T));
    }

    /// <summary>
    /// GH-5136. The handler chain of <paramref name="messageType"/> that belongs to
    /// <paramref name="handlerType"/>, or null
    /// </summary>
    internal HandlerChain? ChainFor(Type messageType, Type handlerType)
    {
        return GroupFor(messageType)?.Chains.FirstOrDefault(x => MessageHandlerGroup.HandlerTypeOf(x) == handlerType);
    }

    /// <summary>
    /// GH-5136. The compiled handler of the chain of <paramref name="messageType"/> that belongs to
    /// <paramref name="handlerType"/>, resolved for the endpoint that chain is bound to if it is bound to one
    /// </summary>
    internal IMessageHandler? HandlerForHandlerType(Type messageType, Type handlerType)
    {
        var chain = ChainFor(messageType, handlerType);
        if (chain == null) return null;

        return chain.Endpoints.Count == 0
            ? HandlerFor(messageType)
            : HandlerFor(messageType, chain.Endpoints[0]);
    }

    public IMessageHandler? HandlerFor(Type messageType, Endpoint endpoint)
    {
        // A manual handler registered for this endpoint only (Endpoint.RegisterStickyMessageHandler) wins
        // over everything else for messages received here. HandlerFor(Type) never looks at it.
        if (endpoint.TryFindStickyMessageHandler(messageType, out var stickyMessageHandler))
        {
            ensureStickyMessageHandlerChain(messageType, stickyMessageHandler);
            return stickyMessageHandler;
        }

        if (messageType.CanBeCastTo(typeof(IAgentCommand)))
        {
            if (_handlers.TryFind(typeof(IAgentCommand), out var handler))
            {
                _handlers = _handlers.AddOrUpdate(messageType, handler);
                return handler;
            }

            throw new NotSupportedException();
        }
        
        // This is cached in the HandlerPipeline for each endpoint, so
        // not terribly important to cache it here
        var group = GroupFor(messageType);

        if (group == null)
        {
            // There are a couple special types where this might be cached. Like for Acknowledgement
            if (_handlers.TryFind(messageType, out var handler)) return handler;

            // This was to handle moving Event<T> to IEvent<T>
            group = groupOrCandidateFor(messageType);
        }

        if (group == null || group.Chains.Count == 0) return null;

        return resolveHandlerFromGroup(messageType, group, endpoint);
    }

    /// <summary>
    /// GH-5137. What runs a message of a type with at least one chain when it is received at
    /// <paramref name="endpoint"/>, in order:
    /// <list type="number">
    /// <item>the chain bound to this endpoint -- a sticky handler, or one of several handler types on its own
    /// local queue. <see cref="AssertOneChainPerEndpoint"/> makes sure there is at most one at startup</item>
    /// <item>the message type's only chain, when it is not bound to any endpoint</item>
    /// <item>a relay to the local queues of the group's chains: from an endpoint outside the process (or a
    /// sharded slot), when every chain is on a local queue, to all of them; from anywhere else -- the message
    /// type's own local queue still holding envelopes from before the handler types were split, a broker
    /// queue next to a handler sticky to another broker queue, or a queue the message was explicitly sent
    /// to -- to the queues of the handler types that are not sticky</item>
    /// <item>otherwise a <see cref="NoHandlerForEndpointException"/> that says where each chain runs</item>
    /// </list>
    /// </summary>
    private IMessageHandler? resolveHandlerFromGroup(Type messageType, MessageHandlerGroup group, Endpoint endpoint)
    {
        var bound = group.Chains.Where(x => x.Endpoints.Contains(endpoint)).ToArray();
        if (bound.Length > 1)
        {
            throw new InvalidOperationException(describeEndpointCollision(messageType, endpoint, bound));
        }

        if (bound.Length == 1)
        {
            return resolveHandlerFromChain(messageType, bound[0], false);
        }

        // The message type's only chain, when it is not bound to any endpoint, runs wherever it is received
        if (group.UnboundChains().Any())
        {
            return HandlerFor(messageType);
        }

        // Every chain is bound to some other endpoint. If they are all local queues, a message from outside the
        // process is relayed to each of them
        var allLocal = group.Chains.All(c => c.Endpoints.All(e => e is LocalQueue));
        if (allLocal && (endpoint is not LocalQueue || endpoint.UsedInShardedTopology))
        {
            return getOrBuildFanoutHandler(messageType, group);
        }

        // GH-5136 / GH-5137. Anywhere else the message is relayed to the queues of the handler types that are not
        // sticky, so each still gets it. That is how envelopes persisted to the message type's own local queue
        // before an upgrade split its handler types drain. A sticky handler runs only at its own endpoints, so a
        // message type whose chains are all sticky still has no handler here.
        var unstickyQueues = group.Chains.Where(x => !x.IsSticky)
            .SelectMany(x => x.Endpoints).OfType<LocalQueue>().Select(x => x.Uri).Distinct().ToArray();

        if (unstickyQueues.Length > 0)
        {
            return BuildFanoutHandler(messageType, group.Chains[0], unstickyQueues);
        }

        throw new NoHandlerForEndpointException(messageType, endpoint.Uri, group);
    }

    /// <summary>
    /// GH-5137. The invariant every executor relies on: at most one handler chain of a message type is bound to
    /// any one endpoint, so the chain that runs a message received there is never a matter of discovery order.
    /// Checked at startup, once the routing conventions have bound their listeners to chains
    /// </summary>
    internal void AssertOneChainPerEndpoint()
    {
        foreach (var group in Groups)
        {
            if (group.Chains.Count < 2) continue;

            var collision = group.Chains
                .SelectMany(chain => chain.Endpoints.Select(endpoint => (endpoint, chain)))
                .GroupBy(x => x.endpoint)
                .FirstOrDefault(x => x.Select(pair => pair.chain).Distinct().Count() > 1);

            if (collision != null)
            {
                throw new InvalidOperationException(describeEndpointCollision(group.MessageType, collision.Key,
                    collision.Select(x => x.chain).Distinct().ToArray()));
            }
        }
    }

    private static string describeEndpointCollision(Type messageType, Endpoint endpoint, IReadOnlyList<HandlerChain> chains)
    {
        return $"Handler types {MessageHandlerGroup.Describe(chains)} each handle message type {messageType.FullNameInCode()} " +
               $"at endpoint {endpoint.Uri}. Wolverine runs one handler chain per handler type, and at most one of them " +
               "may be bound to any one endpoint, so it is clear which one runs a message received there. Make the handlers " +
               "sticky to different endpoints, or merge them into one handler type if they have to run together.";
    }

    public IMessageHandler? HandlerFor(Type messageType)
    {
        if (_handlers.TryFind(messageType, out var handler))
        {
            return handler;
        }

        if (messageType.CanBeCastTo(typeof(IAgentCommand)))
        {
            if (_handlers.TryFind(typeof(IAgentCommand), out handler))
            {
                _handlers = _handlers.AddOrUpdate(messageType, handler);
                return handler;
            }

            throw new NotSupportedException();
        }
        
        // The second half is to handle moving Event<T> to IEvent<T>
        var group = groupOrCandidateFor(messageType);
        if (group != null && group.Chains.Count > 0)
        {
            // GH-5136. One chain per handler type: there is no single handler for a message type handled by
            // several handler types, only one per endpoint
            if (group.Chains.Count > 1)
            {
                throw new NoHandlerForEndpointException(messageType, group);
            }

            return resolveHandlerFromChain(messageType, group.Chains[0], true);
        }

        // memoize the "miss"
        _handlers = _handlers.AddOrUpdate(messageType, null);
        return null;
    }

    private IMessageHandler? resolveHandlerFromChain(Type messageType, HandlerChain chain, bool shouldCacheGlobally)
    {
        IMessageHandler handler;
        if (chain.Handler != null)
        {
            handler = chain.Handler;
        }
        else
        {
            lock (_compilingLock)
            {
                if (chain.Handler != null)
                {
                    handler = chain.Handler;
                }
                else
                {
                    // TODO -- put this logic in JasperFx
                    var logger = Container?.Services.GetService<ILoggerFactory>()?.CreateLogger<HandlerGraph>() ?? new Logger<HandlerGraph>(new LoggerFactory([new DebugLoggerProvider()]));

                    logger.LogDebug("Starting to compile chain {MessageType}", chain.MessageType.NameInCode());

                    chain.InitializeSynchronously(Rules, this, Container!.Services);
                    handler = chain.CreateHandler(Container!);

                    logger.LogDebug("Finished building the chain {MessageType}", chain.MessageType.NameInCode());
                }
            }
        }

        if (shouldCacheGlobally)
        {
            _handlers = _handlers.AddOrUpdate(messageType, handler);
        }

        return handler;
    }

    // Closes FanoutMessageHandler<> over the runtime-resolved messageType
    // and Activator.CreateInstance's the result. Per-message-type lazy-init
    // (cached in _fanoutHandlers ImHashMap after first call). Same chunk J
    // (RoutingFor) pattern: AOT-clean apps in TypeLoadMode.Static pre-
    // populate _handlers at HandlerGraph.Compile time so the steady-state
    // hot path is pure cache lookups. The pre-population work is tracked in
    // #2769 (CloseAndBuildAs elimination).
    private IMessageHandler getOrBuildFanoutHandler(Type messageType, MessageHandlerGroup group)
    {
        if (_fanoutHandlers.TryFind(messageType, out var cached))
        {
            return cached;
        }

        var handler = BuildFanoutHandler(messageType, group.Chains[0], group.BoundLocalQueueUris());

        _fanoutHandlers = _fanoutHandlers.AddOrUpdate(messageType, handler);
        return handler;
    }

    // Closes FanoutMessageHandler<> over the runtime-resolved messageType and
    // Activator.CreateInstance's the result. Used both for sticky-handler fanout
    // (getOrBuildFanoutHandler, cached in _fanoutHandlers) and for relaying an externally-
    // arriving element type to its direct + batch local queues (NOT cached — the
    // _handlers[messageType] slot belongs to the direct handler). AOT-clean apps in
    // TypeLoadMode.Static pre-populate handlers at Compile time. See #2769.
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "FanoutMessageHandler<> closed over runtime messageType; AOT consumers pre-populate handlers via TypeLoadMode.Static. See AOT guide / #2769.")]
    internal IMessageHandler BuildFanoutHandler(Type messageType, HandlerChain chain, Uri[] localQueueUris)
    {
        var handlerType = typeof(FanoutMessageHandler<>).MakeGenericType(messageType);
        return (IMessageHandler)Activator.CreateInstance(handlerType, localQueueUris, chain)!;
    }

    // Compile is the bootstrap-time handler-graph build. The remaining IL2026
    // here comes from registerMessageTypes' MakeGenericType / GetInterfaces walks
    // (pre-population work tracked in #2769). The opt-in Forwarders.FindForwards
    // assembly scan (UseAutomaticForwarderDiscovery) was removed in 7.0 (GH-5198).
    // Suppress at the Compile boundary: a single annotation here keeps
    // WolverineRuntime.StartAsync (the public bootstrap entry point) free of
    // the cascade.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Bootstrap-time handler-graph build; per-message-type cache population (#2769). See AOT guide.")]
    internal void Compile(WolverineOptions options, IServiceContainer container)
    {
        if (_hasCompiled)
        {
            return;
        }

        _hasCompiled = true;

        var logger = (ILogger?)container.Services.GetService<ILogger<HandlerDiscovery>>() ?? NullLogger.Instance;

        Rules = options.CodeGeneration;

        compileWithRuntimeScanning(options, logger);

        Group(options);

        assignDeduplicationScopes();

        // GH-3974: the earliest point at which "will this message type be handled?" has a real answer.
        // Discovery has resolved and the chains are grouped, so a consumer no longer has to hand-roll a
        // mirror of Wolverine's own discovery convention to ask it.
        options.ApplyHandlerDiscoveryCallbacks(Groups.Select(x => x.MessageType));

        // GH-5136. One flat list: every message type's chains, one per handler type
        var allChains = Chains;

        // This lovely thing was brought to you by https://github.com/JasperFx/wolverine/issues/2004. A handler
        // type that handles more than one message type which each have several handler types would otherwise
        // name two chains after the same handler type.
        var duplicateTypeNames = allChains
            .GroupBy(x => x.TypeName)
            .Where(x => x.Count() > 1)
            .ToArray();

        foreach (var @group in duplicateTypeNames)
        {
            foreach (var chain in @group)
            {
                // Both halves have to be sanitized into legal C# identifiers -- the message type may well be
                // an array (batched messages are handled as T[]), which would otherwise emit brackets into
                // the generated class name and blow up compilation. See GH-3399.
                chain.TypeName =
                    $"{HandlerChain.GeneratedTypeNameFor(chain.MessageType, "")}_{HandlerChain.GeneratedTypeNameFor(chain.HandlerCalls().First().HandlerType, "Handler")}";
            }
        }

        foreach (var policy in handlerPolicies(options)) policy.Apply(allChains, Rules, container);

        Container = container;

        // Forwarder registration: drain the explicit
        // WolverineOptions.RegisterMessageForwarder<TFrom, TTo>() registrations
        // (AOT-clean, no reflective assembly walk). The pre-6.0 assembly scan
        // went away in 6.0 (#2757) and its UseAutomaticForwarderDiscovery()
        // opt-in was removed in 7.0 (GH-5198).
        var forwarders = new Forwarders();
        foreach (var pair in options.ExplicitMessageForwarders)
        {
            forwarders.Relationships[pair.Key] = pair.Value;
        }

        AddForwarders(forwarders);

        foreach (var configuration in _configurations) configuration();

        registerMessageTypes();

        tryApplyLocalQueueConfiguration(options);

        options.MessagePartitioning.MaybeInferGrouping(this);

        // Pre-populate the per-message-type cascading-async-enumerable cache on
        // MessageContext so the reflective lazy-init in
        // MessageContext.ResolveTypedAsyncEnumerableCascader never fires at
        // steady state. AOT pillar follow-up #2769 (Option A).
        var allMessageTypes = AllMessageTypes().ToArray();
        MessageContext.PrepopulateCascadeCache(allMessageTypes);

        // Same Option A pre-population for the IntrinsicSerializer<T> cache so
        // ISerializable message types skip the first-occurrence CloseAndBuildAs.
        Wolverine.Runtime.Serialization.IntrinsicSerializer.Instance.Prepopulate(allMessageTypes);
    }

    private void compileWithRuntimeScanning(WolverineOptions options, ILogger logger)
    {
        (Type, MethodInfo)[] methods;

        // Cold-start fast path (Wolverine#1577 Tier 1): in TypeLoadMode.Static, or when the
        // user opts in via UseStaticRegistries(), consume the pre-generated HandlerRegistry
        // instead of scanning assemblies. Never applies during `codegen write` itself — that
        // must run a fresh scan to regenerate the registry accurately.
        if (shouldConsumeStaticRegistry(options) &&
            Discovery.TryLoadStaticHandlerRegistry(options, out var registryTypes))
        {
            logger.LogInformation(
                "Using pre-generated Wolverine HandlerRegistry ({Count} handler types); skipping assembly scan",
                registryTypes.Count);
            methods = Discovery.FindCallsFromTypes(registryTypes, options);
        }
        else
        {
            if (shouldConsumeStaticRegistry(options))
            {
                logger.LogWarning(
                    "Static TypeLoadMode is active but no pre-generated HandlerRegistry was found — falling back to a runtime assembly scan. Run 'dotnet run -- codegen write' to eliminate handler discovery scanning.");
            }

            foreach (var assembly in Discovery.Assemblies)
            {
                logger.LogInformation("Searching assembly {Assembly} for Wolverine message handlers",
                    assembly.GetName());
            }

            methods = Discovery.FindCalls(options);

            warnAboutNearMissHandlerTypes(logger);
        }

        var calls = methods.Select(x => new HandlerCall(x.Item1, x.Item2));

        if (methods.Length == 0)
        {
            logger.LogWarning(
                "Wolverine found no handlers. If this is unexpected, check the assemblies that it's scanning. See https://wolverine.netlify.app/guide/handlers/discovery.html for more information");
        }
        else
        {
            AddRange(calls);
        }
    }

    /// <summary>
    /// GH-4702. Inside the scanning branch only, and after FindCalls has populated the conventional includes.
    /// A plural-named class with handler-shaped methods is otherwise invisible: it never enters the handler
    /// query, so nothing downstream can name it -- the one signal today is an Information-level "No routes
    /// can be determined" long after startup, with no mention of the class.
    /// </summary>
    private void warnAboutNearMissHandlerTypes(ILogger logger)
    {
        foreach (var type in Discovery.FindNearMissHandlerTypes())
        {
            logger.LogWarning(
                "Type {HandlerType} has handler-shaped methods but was not discovered: conventional discovery matches the '{HandlerSuffix}' / '{ConsumerSuffix}' suffix exactly. Rename it to {Suggestion}, mark it [WolverineHandler], or add it with opts.Discovery.IncludeType<{TypeName}>().",
                type.FullNameInCode(), HandlerChain.HandlerSuffix, HandlerChain.ConsumerSuffix,
                type.Name[..^1], type.Name);
        }
    }

    private static bool shouldConsumeStaticRegistry(WolverineOptions options)
    {
        // Regenerating the registry during `codegen write` must always do a fresh scan,
        // otherwise a stale registry would perpetuate itself and miss new handlers.
        if (DynamicCodeBuilder.WithinCodegenCommand)
        {
            return false;
        }

        return options.CodeGeneration.TypeLoadMode == TypeLoadMode.Static || options.UseStaticHandlerRegistry;
    }

    private void tryApplyLocalQueueConfiguration(WolverineOptions options)
    {
        var local = options.Transports.GetOrCreate<LocalTransport>();
        foreach (var chain in Chains)
        {
            local.ApplyConfiguration(chain);
        }
    }

    // Walks chain.MessageType.GetInterfaces() to find interop interfaces,
    // then closes generic-handler types over those interface message types
    // via MakeGenericType. Bootstrap-time helper; AOT consumers preserve
    // interop-interface types via opts.AddMessageInterfaceAssembly(...) +
    // appropriate trim descriptors.
    [UnconditionalSuppressMessage("Trimming", "IL2055",
        Justification = "Closed generic message-handler type for interop interface; bootstrap-time only. See AOT guide.")]
    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "_messageTypes values originate from user-registered message types via RegisterMessageType / HandlerDiscovery. The TypeExtensions.Closes lambda inspects each type's generic-interface graph for MappedGenericMessageTypes keys; user message types are statically rooted via HandlerDiscovery and preserved.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "MessageType.GetInterfaces walk at bootstrap; user message types statically rooted via HandlerDiscovery. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "MakeGenericType over runtime interface type; AOT consumers preserve interop-interface message types via opts.AddMessageInterfaceAssembly. See AOT guide.")]
    private void registerMessageTypes()
    {
        lock (_messageTypesLock)
        {
            _messageTypes =
                _messageTypes.AddOrUpdate(typeof(Acknowledgement).ToMessageTypeName(), typeof(Acknowledgement));

            foreach (var chain in Chains)
            {
                _messageTypes = _messageTypes.AddOrUpdate(chain.MessageType.ToMessageTypeName(), chain.MessageType);

                if (chain.MessageType.TryGetAttribute<InteropMessageAttribute>(out var att))
                {
                    _messageTypes = _messageTypes.AddOrUpdate(att.InteropType.ToMessageTypeName(), chain.MessageType);
                }
                else
                {
                    foreach (var @interface in chain.MessageType.GetInterfaces())
                    {
                        if (InteropAssemblies.Contains(@interface.Assembly))
                        {
                            _messageTypes =
                                _messageTypes.AddOrUpdate(@interface.ToMessageTypeName(), chain.MessageType);
                        }
                    }
                }
            }
        }

        foreach (var pair in MappedGenericMessageTypes)
        {
            var matches = _messageTypes.Enumerate().Select(x => x.Value).Where(x => x.Closes(pair.Key));
            foreach (var interfaceType in matches)
            {
                var closedType = pair.Value.MakeGenericType(interfaceType.GetGenericArguments());
                RegisterMessageType(closedType);
            }
        }
    }

    /// <remarks>
    ///     GH-3911: <see cref="EagerIdempotencyOnNonTransactionalChains" /> is deliberately pulled to the
    ///     END, whatever order it was registered in. Its whole semantic is "for chains that nothing else
    ///     made transactional", so it can only answer that question once every other policy has had its
    ///     say. Left in registration order it produced generated code that varied by the order the user
    ///     happened to call <c>AutoApplyIdempotencyOnNonTransactionalHandlers()</c> relative to a store's
    ///     <c>IntegrateWithWolverine()</c> — which is why the store op policies could not set
    ///     <c>IsTransactional</c> at all before this.
    /// </remarks>
    private IEnumerable<IHandlerPolicy> handlerPolicies(WolverineOptions options)
    {
        // GH-4180: DeduplicationHandlerPolicy is pulled to the end for exactly the same reason as the
        // idempotency policy beside it -- the frames it weaves differ depending on IsTransactional, so
        // it can only run once every store's policy has had its say.
        var registered = options.RegisteredPolicies
            .OrderBy(x => x is EagerIdempotencyOnNonTransactionalChains or DeduplicationHandlerPolicy ? 1 : 0)
            .ToArray();

        foreach (var policy in registered)
        {
            if (policy is IHandlerPolicy h)
            {
                yield return h;
            }

            if (policy is IChainPolicy c)
            {
                yield return new HandlerChainPolicy(c);
            }
        }
    }

    public bool TryFindMessageType(string messageTypeName, out Type messageType)
    {
        return _messageTypes.TryFind(messageTypeName, out messageType);
    }

    public void Group(WolverineOptions options)
    {
        lock (_groupingLock)
        {
            if (_hasGrouped)
            {
                return;
            }

            foreach (var messageGroup in _calls.GroupBy(x => x.MessageType))
            {
                var group = buildHandlerGroup(options, messageGroup);
                _chains = _chains.AddOrUpdate(group.MessageType, group);
            }


            _hasGrouped = true;
        }
    }

    private bool isSagaMethod(HandlerCall call)
    {
        if (call.HandlerType.CanBeCastTo<Saga>())
        {
            if (call.Method.Name == SagaChain.NotFound) return true;
            
            // Legal for Start() methods to be be a static 
            if (!call.Method.IsStatic) return true;

            if (call.Method.Name.EqualsIgnoreCase("Start")) return true;
            if (call.Method.Name.EqualsIgnoreCase("StartAsync")) return true;
        }

        return false;
    }

    /// <summary>
    /// GH-5136. One chain per (message type, handler type). A saga type's Start / Handle / NotFound methods for
    /// the message are one <see cref="SagaChain"/>; any other handler type gets a <see cref="HandlerChain"/> of
    /// its own, including one that sits beside a saga. With more than one chain, each chain that is not sticky
    /// to an endpoint gets the local queue named for its handler type and a generated type named for its handler
    /// type. A message type with a single chain keeps the message type's queue and generated type name.
    /// </summary>
    private MessageHandlerGroup buildHandlerGroup(WolverineOptions options, IGrouping<Type, HandlerCall> calls)
    {
        var group = new MessageHandlerGroup(calls.Key);

        foreach (var byHandlerType in calls.GroupBy(x => x.HandlerType))
        {
            assertOneMethodPerNonSagaHandlerType(calls.Key, byHandlerType);

            var handlerCalls = new HandlerCallGrouping(calls.Key, byHandlerType.ToArray());

            // If the SagaChain handler method is a static, then it's valid to be a "Start" method
            HandlerChain chain = handlerCalls.Any(isSagaMethod)
                ? new SagaChain(options, handlerCalls, this)
                : new HandlerChain(options, handlerCalls, this);

            group.Add(chain);
        }

        var separated = group.Chains.Count > 1;

        foreach (var chain in group.Chains)
        {
            // Sticky handlers always apply, whether or not the message type has any other handler
            var handlerCall = chain.HandlerCalls().First();
            var assigned = chain.TryAssignStickyEndpoints(options);

            if (!separated) continue;

            if (!assigned)
            {
                chain.RegisterEndpoint(options.Transports.GetOrCreate<LocalTransport>()
                    .QueueFor(handlerCall.HandlerType.FullNameInCode().ToLowerInvariant()));
            }

            chain.TypeName = HandlerChain.GeneratedTypeNameFor(handlerCall.HandlerType, HandlerChain.HandlerSuffix);
            chain.OverrideDescription(
                $"Message Handler for {chain.MessageType.FullNameInCode()} using {handlerCall.HandlerType.FullNameInCode()}");
        }

        return group;
    }

    /// <summary>
    /// GH-5136. Two methods of one handler class for the same message type would be one chain running both, the
    /// combining that Wolverine 7 removed everywhere else. A saga is exempt: its Start, Handle and NotFound
    /// methods for a message are one saga chain by design.
    /// </summary>
    private static void assertOneMethodPerNonSagaHandlerType(Type messageType, IGrouping<Type, HandlerCall> calls)
    {
        if (calls.Key.CanBeCastTo<Saga>()) return;

        var methods = calls.Select(x => x.Method).Distinct().ToArray();
        if (methods.Length < 2) return;

        throw new InvalidOperationException(
            $"Handler type {calls.Key.FullNameInCode()} has {methods.Length} methods that handle message type {messageType.FullNameInCode()} " +
            $"({methods.Select(x => x.Name + "()").Join(", ")}). A handler type may have only one handler method for each message type. " +
            "Merge them into one method, or move one of them to a different handler type to give it a handler chain of its own.");
    }

    private sealed class HandlerCallGrouping(Type key, HandlerCall[] calls) : IGrouping<Type, HandlerCall>
    {
        public Type Key { get; } = key;
        public IEnumerator<HandlerCall> GetEnumerator() => ((IEnumerable<HandlerCall>)calls).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => calls.GetEnumerator();
    }

    // typeof(ForwardingHandler<,>).CloseAndBuildAs<MessageHandler>(this, source, destination)
    // closes the forwarding-handler generic over (source, destination) message
    // types. AddForwarders runs at bootstrap from HandlerGraph.Compile (or via
    // the IForwardsTo<> assembly scan whose elimination is tracked in #2757).
    // Same chunk D / I / J / K CloseAndBuildAs pattern.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "ForwardingHandler<,> closed over runtime (source, destination) message types at bootstrap; user types statically rooted. See AOT guide / #2757.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "ForwardingHandler<,> closed over runtime (source, destination) message types at bootstrap; user types statically rooted. See AOT guide / #2757.")]
    internal void AddForwarders(Forwarders forwarders)
    {
        foreach (var pair in forwarders.Relationships)
        {
            var source = pair.Key;
            var destination = pair.Value;

            if (_chains.TryFind(destination, out _))
            {
                var handler =
                    typeof(ForwardingHandler<,>).CloseAndBuildAs<MessageHandler>(this, source, destination);

                _chains = _chains.AddOrUpdate(source, new MessageHandlerGroup(source, handler.Chain!));
                _handlers = _handlers.AddOrUpdate(source, handler);
            }
        }
    }

    public bool CanHandle(Type messageType)
    {
        return _chains.TryFind(messageType, out _) || _handlers.Contains(messageType);
    }

    public void RegisterMessageType(Type messageType)
    {
        if (_replyTypes.Contains(messageType))
        {
            return;
        }

        lock (_messageTypesLock)
        {
            _messageTypes = _messageTypes.AddOrUpdate(messageType.ToMessageTypeName(), messageType);
            _replyTypes = _replyTypes.Add(messageType);
        }
    }
    
    public void RegisterMessageType(Type messageType, string messageAlias)
    {
        lock (_messageTypesLock)
        {
            if (_messageTypes.TryFind(messageAlias, out var existing))
            {
                // GH-4532: the old message interpolated the type it found in the map, not the one being
                // registered, so it named the wrong half of the collision -- and said nothing about
                // [MessageIdentity], which is how you resolve one.
                throw new InvalidOperationException(
                    $"Message types {existing.FullNameInCode()} and {messageType.FullNameInCode()} both resolve to the message alias '{messageAlias}'. " +
                    "Aliases must be unique because the alias is what Wolverine writes on the wire to identify the message type. " +
                    $"Give one of them a distinct alias with [{nameof(MessageIdentityAttribute).Replace("Attribute", "")}(\"...\")], or rename the type.");
            }

            _messageTypes = _messageTypes.AddOrUpdate(messageAlias, messageType);
            _replyTypes = _replyTypes.Add(messageType);
        }
    }

    // GH-5133. A message type with more than one chain -- one per handler type -- would otherwise have every
    // chain claim the same logical deduplication id, so the first claim wins and the rest drop the message.
    // Give each such chain its own scope. Done here, straight after grouping, because SagaChain clears its
    // handler calls when it generates code.
    private void assignDeduplicationScopes()
    {
        foreach (var group in Groups)
        {
            if (group.Chains.Count < 2) continue;

            foreach (var chain in group.Chains)
            {
                var handlerTypes = chain.Handlers.Select(x => x.HandlerType.FullNameInCode()).Distinct()
                    .OrderBy(x => x).Join("+");

                chain.DeduplicationScope = Persistence.Codegen.ScopeHandlerDeduplicationIdFrame.SuffixFor(handlerTypes);
            }
        }
    }

    /// <summary>
    /// GH-5133 / GH-5136. Every chain in the graph, the same flat list as <see cref="Chains"/>
    /// </summary>
    internal IEnumerable<HandlerChain> EveryChain() => Chains;

    /// <summary>
    /// GH-5133. Every saga chain, including one per saga type when more than one saga type handles the same
    /// message. Saga store diagnostics and validators read this so that a saga type
    /// whose messages are all shared with another saga is not missed.
    /// </summary>
    internal IEnumerable<SagaChain> SagaChains() => EveryChain().OfType<SagaChain>();

    /// <summary>
    /// GH-4060 / GH-5133. Is any requeue-shaped error handling configured, globally or on any chain?
    /// </summary>
    internal bool AnyRequeuePolicies()
        => Failures.AnyRequeuePolicies() || EveryChain().Any(x => x.Failures.AnyRequeuePolicies());

    /// <summary>
    /// GH-5133. The chains that hold handlers from more than one handler class for the same message type.
    /// GH-5136: Wolverine 7 always builds one chain per handler type, so there are none.
    /// </summary>
    internal HandlerChain[] CombinedHandlerChains() => [];

    /// <summary>
    /// Every handler chain in the graph, one per (message type, handler type). The same list as <see cref="Chains"/>
    /// </summary>
    public IEnumerable<HandlerChain> AllChains() => Chains;

    public IEnumerable<Type> AllMessageTypes()
    {
        foreach (var group in Groups)
        {
            yield return group.MessageType;

            foreach (var publishedType in group.Chains.SelectMany(x => x.PublishedTypes())) yield return publishedType;
        }

        foreach (var entry in _messageTypes.Enumerate())
        {
            yield return entry.Value;
        }
    }

}