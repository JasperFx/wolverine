using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Baseline;
using Baseline.ImTools;
using Wolverine.Util;
using Lamar;
using LamarCodeGeneration;
using LamarCompiler;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Middleware;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime.ResponseReply;
using Wolverine.Runtime.Scheduled;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.Runtime.Handlers;

public partial class HandlerGraph : ICodeFileCollection, IHandlerConfiguration
{
    public static readonly string Context = "context";
    private readonly List<HandlerCall> _calls = new();

    private readonly IList<Action> _configurations = new List<Action>();
    private readonly IList<IHandlerPolicy> _policies = new List<IHandlerPolicy>();

    private readonly object _groupingLock = new();
    private readonly object _compilingLock = new();

    internal readonly HandlerSource Source = new();

    private ImHashMap<Type, HandlerChain> _chains = ImHashMap<Type, HandlerChain>.Empty;

    private ImHashMap<Type, MessageHandler?> _handlers = ImHashMap<Type, MessageHandler?>.Empty;

    private bool _hasGrouped;

    private ImHashMap<string, Type> _messageTypes = ImHashMap<string, Type>.Empty;

    public HandlerGraph()
    {
        // All of this is to seed the handler and its associated retry policies
        // for scheduling outgoing messages
        AddMessageHandler(typeof(Envelope), new ScheduledSendEnvelopeHandler(this));

        _messageTypes = _messageTypes.AddOrUpdate(TransportConstants.ScheduledEnvelope, typeof(Envelope));
        
        RegisterMessageType(typeof(Acknowledgement));
        RegisterMessageType(typeof(FailureAcknowledgement));
    }

    internal void AddMessageHandler(Type messageType, MessageHandler handler)
    {
        _handlers = _handlers.AddOrUpdate(messageType, handler);
    }
    
    internal IContainer? Container { get; set; }

    public HandlerChain[] Chains => _chains.Enumerate().Select(x => x.Value).ToArray();

    public FailureRuleCollection Failures { get; set; } = new();

    public void AddMiddleware<T>(Func<HandlerChain, bool>? filter = null)
    {
        AddMiddleware(typeof(T), filter);
    }

    public void AddMiddleware(Type middlewareType, Func<HandlerChain, bool>? filter = null)
    {
        var policy = findOrCreateMiddlewarePolicy();

        policy.AddType(middlewareType, filter);
    }

    private MiddlewarePolicy findOrCreateMiddlewarePolicy()
    {
        var policy = _policies.OfType<MiddlewarePolicy>().FirstOrDefault();
        if (policy == null)
        {
            policy = new MiddlewarePolicy();
            _policies.Add(policy);
        }

        return policy;
    }

    public IHandlerConfiguration Discovery(Action<HandlerSource> configure)
    {
        configure(Source);
        return this;
    }

    /// <summary>
    ///     Applies a handler policy to all known message handlers
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public void AddPolicy<T>() where T : IHandlerPolicy, new()
    {
        AddPolicy(new T());
    }

    /// <summary>
    ///     Applies a handler policy to all known message handlers
    /// </summary>
    /// <param name="policy"></param>
    public void AddPolicy(IHandlerPolicy policy)
    {
        _policies.Add(policy);
    }

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


    public MessageHandler? HandlerFor<T>()
    {
        return HandlerFor(typeof(T));
    }

    public HandlerChain? ChainFor(Type messageType)
    {
        return HandlerFor(messageType)?.Chain;
    }

    public HandlerChain? ChainFor<T>()
    {
        return ChainFor(typeof(T));
    }


    public MessageHandler? HandlerFor(Type messageType)
    {
        if (_handlers.TryFind(messageType, out var handler))
        {
            return handler;
        }

        if (_chains.TryFind(messageType, out var chain))
        {
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
                        chain.InitializeSynchronously(Rules!, this, Container);
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

        // memoize the "miss"
        _handlers = _handlers.AddOrUpdate(messageType, null);
        return null;
    }


    internal async Task CompileAsync(WolverineOptions options, IContainer container)
    {
        Rules = options.Advanced.CodeGeneration;
        var calls = await Source.FindCallsAsync(options);

        if (calls.Any())
        {
            AddRange(calls);
        }

        Group();

        foreach (var policy in _policies) policy.Apply(this, Rules, container);

        Container = container;

        var forwarders = new Forwarders();
        await forwarders.FindForwardsAsync(options.ApplicationAssembly!);
        AddForwarders(forwarders);

        foreach (var configuration in _configurations) configuration();

        _messageTypes =
            _messageTypes.AddOrUpdate(typeof(Acknowledgement).ToMessageTypeName(), typeof(Acknowledgement));

        foreach (var chain in Chains)
        {
            _messageTypes = _messageTypes.AddOrUpdate(chain.MessageType.ToMessageTypeName(), chain.MessageType);
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

            _calls.Where(x => x.MessageType.IsConcrete())
                .GroupBy(x => x.MessageType)
                .Select(buildHandlerChain)
                .Each(chain => { _chains = _chains.AddOrUpdate(chain.MessageType, chain); });


            _hasGrouped = true;
        }
    }

    private HandlerChain buildHandlerChain(IGrouping<Type, HandlerCall> @group)
    {
        if (@group.Any(x => x.HandlerType.CanBeCastTo<Saga>()))
        {
            return new SagaChain(@group, this);
        }

        return new HandlerChain(@group, this);
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
        return _chains.TryFind(messageType, out _);
    }
    
    private ImmutableList<Type> _replyTypes = ImmutableList<Type>.Empty;

    public void RegisterMessageType(Type messageType)
    {
        if (_replyTypes.Contains(messageType)) return;
        _messageTypes = _messageTypes.AddOrUpdate(messageType.ToMessageTypeName(), messageType);
        _replyTypes = _replyTypes.Add(messageType);
    }

    public void AddMiddlewareByMessageType(Type middlewareType)
    {
        var policy = findOrCreateMiddlewarePolicy();

        var application = policy.AddType(middlewareType);
        application.MatchByMessageType = true;
    }
}
