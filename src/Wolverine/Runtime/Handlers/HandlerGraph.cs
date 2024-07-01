﻿using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.RuntimeCompiler;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Runtime.Scheduled;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Util;

namespace Wolverine.Runtime.Handlers;

public partial class HandlerGraph : ICodeFileCollectionWithServices, IWithFailurePolicies
{
    public static readonly string Context = "context";
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

    internal IServiceContainer Container { get; set; }

    public HandlerChain[] Chains => _chains.Enumerate().Select(x => x.Value).ToArray();

    public IEnumerable<Assembly> ExtensionAssemblies => Discovery.Assemblies;
    public List<Assembly> InteropAssemblies { get; } = new();

    public FailureRuleCollection Failures { get; set; } = new();

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
        return (HandlerFor(messageType) as MessageHandler)?.Chain;
    }

    public HandlerChain? ChainFor<T>()
    {
        return ChainFor(typeof(T));
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
            return resolveHandlerFromChain(messageType, chain);
        }

        // This was to handle moving Event<T> to IEvent<T>
        var candidates = _chains.Enumerate().Where(x => messageType.CanBeCastTo(x.Key)).ToArray();
        if (candidates.Length == 1)
        {
            chain = candidates[0].Value;
            return resolveHandlerFromChain(messageType, chain);
        }

        // memoize the "miss"
        _handlers = _handlers.AddOrUpdate(messageType, null);
        return null;
    }

    private IMessageHandler? resolveHandlerFromChain(Type messageType, HandlerChain chain)
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
                Debug.WriteLine("Starting to compile chain " + chain.MessageType.NameInCode());
                if (chain.Handler == null)
                {
                    chain.InitializeSynchronously(Rules, this, Container.Services);
                    handler = chain.CreateHandler(Container!);
                }
                else
                {
                    handler = chain.Handler;
                }

                Debug.WriteLine("Finished building the chain " + chain.MessageType.NameInCode());
            }
        }

        _handlers = _handlers.AddOrUpdate(messageType, handler);

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

        Group();

        foreach (var policy in handlerPolicies(options)) policy.Apply(Chains, Rules, container);

        Container = container;

        var forwarders = new Forwarders();
        forwarders.FindForwards(options.ApplicationAssembly!);
        AddForwarders(forwarders);

        foreach (var configuration in _configurations) configuration();

        registerMessageTypes();
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

    public void Group()
    {
        lock (_groupingLock)
        {
            if (_hasGrouped)
            {
                return;
            }

            _calls
                .GroupBy(x => x.MessageType)
                .Select(buildHandlerChain)
                .Each(chain => { _chains = _chains.AddOrUpdate(chain.MessageType, chain); });


            _hasGrouped = true;
        }
    }

    private bool isSagaMethod(HandlerCall call)
    {
        if (call.HandlerType.CanBeCastTo<Saga>())
        {
            if (!call.Method.IsStatic) return true;

            if (call.Method.Name.EqualsIgnoreCase("Start")) return true;
            if (call.Method.Name.EqualsIgnoreCase("StartAsync")) return true;
        }

        return false;
    }

    private HandlerChain buildHandlerChain(IGrouping<Type, HandlerCall> group)
    {
        // If the SagaChain handler method is a static, then it's valid to be a "Start" method
        if (group.Any(isSagaMethod))
        {
            return new SagaChain(group, this);
        }

        return new HandlerChain(group, this);
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
}