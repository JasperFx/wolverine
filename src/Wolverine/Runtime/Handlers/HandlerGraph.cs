using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using ImTools;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.RuntimeCompiler;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Logging.Debug;
using Microsoft.Extensions.Options;
using Wolverine.Attributes;
using Wolverine.Configuration;
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

    private ImHashMap<Type, HandlerChain> _chains = ImHashMap<Type, HandlerChain>.Empty;

    private ImHashMap<Type, IMessageHandler?> _handlers = ImHashMap<Type, IMessageHandler?>.Empty;

    private bool _hasCompiled;

    private bool _hasGrouped;

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

    internal IServiceContainer Container { get; set; }

    public HandlerChain[] Chains => _chains.Enumerate().Select(x => x.Value).ToArray();

    public IEnumerable<Assembly> ExtensionAssemblies => Discovery.Assemblies;
    public List<Assembly> InteropAssemblies { get; } = new();

    public FailureRuleCollection Failures { get; set; } = new();

    public MultipleHandlerBehavior MultipleHandlerBehavior { get; set; } =
        MultipleHandlerBehavior.ClassicCombineIntoOneLogicalHandler;

    public void ConfigureHandlerForMessage<T>(Action<HandlerChain> configure)
    {
        ConfigureHandlerForMessage(typeof(T), configure);
    }

    public void ConfigureHandlerForMessage(Type messageType, Action<HandlerChain> configure)
    {
        _configurations.Add(() =>
        {
            var chain = ChainFor(messageType);
            if (chain != null)
            {
                configure(chain);
            }
        });
    }

    internal void AddMessageHandler(Type messageType, IMessageHandler handler)
    {
        _handlers = _handlers.AddOrUpdate(messageType, handler);
        RegisterMessageType(messageType);
    }

    private void assertNotGrouped()
    {
        if (_hasGrouped)
        {
            throw new InvalidOperationException("This HandlerGraph has already been grouped/compiled");
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

    public HandlerChain? ChainFor(Type messageType)
    {
        if (_chains.TryFind(messageType, out var chain)) return chain;

        return null;
    }

    public HandlerChain? ChainFor<T>()
    {
        return ChainFor(typeof(T));
    }

    public IMessageHandler? HandlerFor(Type messageType, Endpoint endpoint)
    {
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
        var chain = ChainFor(messageType);

        if (chain == null)
        {
            // There are a couple special types where this might be cached. Like for Acknowledgement
            if (_handlers.TryFind(messageType, out var handler)) return handler;
            
            // This was to handle moving Event<T> to IEvent<T>
            var candidates = _chains.Enumerate().Where(x => messageType.CanBeCastTo(x.Key)).ToArray();
            if (candidates.Length == 1)
            {
                chain = candidates[0].Value;
            }
        }
        
        if (chain == null) return null;

        // If there are no sticky handlers, just use the default handler
        // for the message type
        if (!chain.ByEndpoint.Any()) return HandlerFor(messageType);

        // See if there is a sticky handler that is specific to this endpoint
        var sticky = chain.ByEndpoint.FirstOrDefault(x => x.Endpoints.Contains(endpoint));
        
        // If none, use the default
        if (sticky == null)
        {
            if (!chain.HasDefaultNonStickyHandlers())
            {
                throw new NoHandlerForEndpointException(messageType, endpoint.Uri);
            }
            
            return HandlerFor(messageType);
        }

        return resolveHandlerFromChain(messageType, sticky, false);
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
        
        if (_chains.TryFind(messageType, out var chain))
        {
            return resolveHandlerFromChain(messageType, chain, true);
        }

        // This was to handle moving Event<T> to IEvent<T>
        var candidates = _chains.Enumerate().Where(x => messageType.CanBeCastTo(x.Key)).ToArray();
        if (candidates.Length == 1)
        {
            chain = candidates[0].Value;
            return resolveHandlerFromChain(messageType, chain, true);
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
        else if (!chain.HasDefaultNonStickyHandlers())
        {
            throw new NoHandlerForEndpointException(messageType);
        }
        else
        {
            lock (_compilingLock)
            {
                // TODO -- put this logic in JasperFx
                var logger = Container?.Services.GetService<ILoggerFactory>()?.CreateLogger<HandlerGraph>() ?? new Logger<HandlerGraph>(new LoggerFactory([new DebugLoggerProvider()]));
                
                logger.LogDebug("Starting to compile chain {MessageType}", chain.MessageType.NameInCode());

                if (chain.Handler == null)
                {
                    chain.InitializeSynchronously(Rules, this, Container.Services);
                    handler = chain.CreateHandler(Container!);
                }
                else
                {
                    handler = chain.Handler;
                }

                logger.LogDebug("Finished building the chain {MessageType}", chain.MessageType.NameInCode());
            }
        }

        if (shouldCacheGlobally)
        {
            _handlers = _handlers.AddOrUpdate(messageType, handler);
        }

        return handler;
    }

    internal void Compile(WolverineOptions options, IServiceContainer container)
    {
        if (_hasCompiled)
        {
            return;
        }

        _hasCompiled = true;

        var logger = (ILogger)container.Services.GetService<ILogger<HandlerDiscovery>>() ?? NullLogger.Instance;

        Rules = options.CodeGeneration;

        foreach (var assembly in Discovery.Assemblies)
        {
            logger.LogInformation("Searching assembly {Assembly} for Wolverine message handlers", assembly.GetName());
        }

        var methods = Discovery.FindCalls(options);

        var calls = methods.Select(x => new HandlerCall(x.Item1, x.Item2));

        if (methods.Length == 0)
        {
            logger.LogWarning("Wolverine found no handlers. If this is unexpected, check the assemblies that it's scanning. See https://wolverine.netlify.app/guide/handlers/discovery.html for more information");
        }
        else
        {
            AddRange(calls);
        }

        Group(options);

        // This was to address the issue with policies not extending to sticky message
        // handlers
        IEnumerable<HandlerChain> explodeChains(HandlerChain chain)
        {
            yield return chain;

            foreach (var stickyChain in chain.ByEndpoint)
            {
                yield return stickyChain;
            }
        }

        var allChains = Chains.SelectMany(explodeChains).ToArray();

        foreach (var policy in handlerPolicies(options)) policy.Apply(allChains, Rules, container);

        Container = container;

        var forwarders = new Forwarders();
        forwarders.FindForwards(options.ApplicationAssembly!);
        AddForwarders(forwarders);

        foreach (var configuration in _configurations) configuration();

        registerMessageTypes();

        tryApplyLocalQueueConfiguration(options);
    }

    private void tryApplyLocalQueueConfiguration(WolverineOptions options)
    {
        var local = options.Transports.GetOrCreate<LocalTransport>();
        foreach (var chain in Chains)
        {
            local.ApplyConfiguration(chain);
        }
    }

    private void registerMessageTypes()
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
                        _messageTypes = _messageTypes.AddOrUpdate(@interface.ToMessageTypeName(), chain.MessageType);
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

    private IEnumerable<IHandlerPolicy> handlerPolicies(WolverineOptions options)
    {
        foreach (var policy in options.RegisteredPolicies)
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

            _calls
                .GroupBy(x => x.MessageType)
                .Select(x => buildHandlerChain(options, x))
                .Each(chain => { _chains = _chains.AddOrUpdate(chain.MessageType, chain); });


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

    private HandlerChain buildHandlerChain(WolverineOptions options, IGrouping<Type, HandlerCall> group)
    {
        // If the SagaChain handler method is a static, then it's valid to be a "Start" method
        if (group.Any(isSagaMethod))
        {
            return new SagaChain(options, group, this);
        }

        return new HandlerChain(options, group, this);
    }

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

                _chains = _chains.AddOrUpdate(source, handler.Chain!);
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

        _messageTypes = _messageTypes.AddOrUpdate(messageType.ToMessageTypeName(), messageType);
        _replyTypes = _replyTypes.Add(messageType);
    }
    
    public void RegisterMessageType(Type messageType, string messageAlias)
    {
        if (_messageTypes.TryFind(messageAlias, out var type))
        {
            throw new InvalidOperationException($"Cannot register type {type} with alias {messageAlias} because alias is already used");
        }

        if (_replyTypes.Contains(messageType))
        {
            return;
        }

        _messageTypes = _messageTypes.AddOrUpdate(messageAlias, messageType);
        _replyTypes = _replyTypes.Add(messageType);
    }

    public IEnumerable<HandlerChain> AllChains()
    {
        foreach (var chain in Chains)
        {
            if (chain.Handlers.Any()) yield return chain;

            foreach (var handlerChain in chain.ByEndpoint)
            {
                yield return handlerChain;
            }
        }
    }

    public IEnumerable<Type> AllMessageTypes()
    {
        foreach (var chain in Chains)
        {
            yield return chain.MessageType;

            foreach (var publishedType in chain.PublishedTypes()) yield return publishedType;
        }

        foreach (var entry in _messageTypes.Enumerate())
        {
            yield return entry.Value;
        }
    }
}