using System.Diagnostics.CodeAnalysis;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Runtime.Routing;
using Wolverine.Util;

namespace Wolverine.Transports;

public abstract class MessageRoutingConvention<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransport, TListener, TSubscriber, TSelf> : IMessageRoutingConvention
    where TTransport : IBrokerTransport, new()
    where TSelf : MessageRoutingConvention<TTransport, TListener, TSubscriber, TSelf>
    where TSubscriber : IDelayedEndpointConfiguration
{
    /// <summary>
    ///     Optionally include (allow list) or exclude (deny list) types. By default, this will apply to all message types
    /// </summary>
    private readonly Util.CompositeFilter<Type> _typeFilters = new();

    private Action<TListener, MessageRoutingContext> _configureListener = (_, _) => { };
    private Action<TSubscriber, MessageRoutingContext> _configureSending = (_, _) => { };
    protected Func<Type, string?> _identifierForSender = t => t.ToMessageTypeName();
    protected Func<Type, string?> _queueNameForListener = t => t.ToMessageTypeName();
    private NamingSource _namingSource = NamingSource.FromMessageType;

    /// <summary>
    /// Tracks message types whose sender configuration has already been applied so that
    /// <see cref="_configureSending"/> doesn't run twice for a given message type when
    /// <see cref="DiscoverSenders"/> is later called following an earlier
    /// <see cref="PreregisterSenders"/> call. See GH-2588.
    /// </summary>
    private readonly HashSet<Type> _configuredSenders = new();

    /// <summary>
    /// Guards the non-thread-safe sender-registration state (<see cref="_configuredSenders"/>
    /// and the subscriber endpoint's Subscriptions list). <c>tryRegisterSenderConfiguration</c>
    /// is reachable both from <see cref="PreregisterSenders"/> during host startup and from
    /// the lazy <see cref="DiscoverSenders"/> on the first publish path; if a publish races
    /// the tail of startup these run concurrently on the same convention instance and corrupt
    /// the <see cref="HashSet{T}"/>. See GH-2874.
    /// </summary>
    private readonly object _senderRegistrationLock = new();

    /// <summary>
    /// The specific transport instance this convention should route against, set by
    /// transport-specific <c>UseConventionalRouting()</c> methods (e.g. on
    /// <see cref="BrokerExpression{TTransport,TListenerEndpoint,TSubscriberEndpoint,TListenerExpression,TSubscriber,TSelf}"/>
    /// subclasses) to the broker they were configured against. When null, falls back to the
    /// default transport instance for <typeparamref name="TTransport"/>. Without this, conventional
    /// routing configured against a named broker would silently discover listeners and senders
    /// against the default broker instead. See GH-3633.
    /// </summary>
    public TTransport? BoundTransport { get; set; }

    private TTransport resolveTransport(IWolverineRuntime runtime)
        => BoundTransport ?? runtime.Options.Transports.GetOrCreate<TTransport>();

    void IMessageRoutingConvention.DiscoverListeners(IWolverineRuntime runtime, IReadOnlyList<Type> handledMessageTypes)
    {
        if(_onlyApplyToOutboundMessages)
        {
            return;
        }

        var transport = resolveTransport(runtime);

        foreach (var messageType in handledMessageTypes.Where(t => _typeFilters.Matches(t)))
        {
            var chain = runtime.Options.HandlerGraph.ChainFor(messageType);

            // Batch element types won't have their own handler chain (only the array type does),
            // but they still need external listeners created so messages can be received and
            // routed to the local batching queue. See GH-2307.
            var isBatchElementType = runtime.Options.BatchDefinitions.Any(b => b.ElementType == messageType);

            if (chain == null)
            {
                if (isBatchElementType)
                {
                    maybeCreateListenerForMessageOrHandlerType(transport, messageType, runtime);
                }

                continue;
            }

            if (_namingSource == NamingSource.FromHandlerType && (chain.Handlers.Any() || chain.ByEndpoint.Any()))
            {
                foreach (var handler in chain.Handlers)
                {
                    var handlerType = handler.HandlerType;
                    var endpoint = maybeCreateListenerForMessageOrHandlerType(transport, handlerType, runtime, messageType);
                    if (endpoint != null)
                    {
                        endpoint.StickyHandlers.Add(handlerType);
                    }
                }

                // Separated handler chains live in ByEndpoint, NOT chain.Handlers. When a message is
                // handled by more than one handler under MultipleHandlerBehavior.Separated (e.g. a saga
                // plus a regular handler), only the saga stays in chain.Handlers - the sibling handlers
                // are moved to ByEndpoint. They each still need their own handler-named listener, or the
                // message never reaches them and only chain.Handlers (the saga) is invoked. GH-3041.
                foreach (var handlerChain in chain.ByEndpoint)
                {
                    var handlerType = handlerChain.Handlers.First().HandlerType;
                    var endpoint = maybeCreateListenerForMessageOrHandlerType(transport, handlerType, runtime, messageType);
                    if (endpoint != null)
                    {
                        handlerChain.RegisterEndpoint(endpoint);
                        endpoint.StickyHandlers.Add(handlerType);
                    }
                }
            }
            else if (runtime.Options.MultipleHandlerBehavior == MultipleHandlerBehavior.ClassicCombineIntoOneLogicalHandler && chain.Handlers.Any())
            {
                maybeCreateListenerForMessageOrHandlerType(transport, messageType, runtime);
            }
            else if (runtime.Options.MultipleHandlerBehavior == MultipleHandlerBehavior.Separated)
            {
                if (chain.Handlers.Any())
                {
                    maybeCreateListenerForMessageOrHandlerType(transport, messageType, runtime);
                }

                foreach (var handlerChain in chain.ByEndpoint)
                {
                    var handlerType = handlerChain.Handlers.First().HandlerType;
                    var endpoint = maybeCreateListenerForMessageAndSeparatedHandlerType(transport, messageType, handlerType, runtime);
                    if (endpoint != null)
                    {
                        handlerChain.RegisterEndpoint(endpoint);
                        endpoint.StickyHandlers.Add(handlerType);
                    }
                }
            }


        }
    }

    private Endpoint? maybeCreateListenerForMessageAndSeparatedHandlerType(TTransport transport, Type messageType, Type handlerType, IWolverineRuntime runtime)
    {
        // Can be null, so bail out if there's no queue
        var topicName = _queueNameForListener(messageType);
        if (topicName.IsEmpty())
        {
            return null;
        }

        var corrected = transport.MaybeCorrectName(topicName);

        var (configuration, endpoint) = FindOrCreateListenerForIdentifierUsingSeparatedHandler(corrected, transport, messageType, handlerType);
        //endpoint.EndpointName = queueName;

        endpoint.IsListener = true;

        var context = new MessageRoutingContext(messageType, runtime);

        _configureListener(configuration, context);

        configuration!.As<IDelayedEndpointConfiguration>().Apply();
            
        ApplyListenerRoutingDefaults(endpoint.EndpointName, transport, messageType);

        return endpoint;
    }

    private Endpoint? maybeCreateListenerForMessageOrHandlerType(TTransport transport, Type messageOrHandlerType, IWolverineRuntime runtime, Type? originalMessageType = null)
    {
        // Can be null, so bail out if there's no queue
        var queueName = _queueNameForListener(messageOrHandlerType);
        if (queueName.IsEmpty())
        {
            return null;
        }

        var corrected = transport.MaybeCorrectName(queueName);

        var (configuration, endpoint) = FindOrCreateListenerForIdentifier(corrected, transport, messageOrHandlerType);
        //endpoint.EndpointName = queueName;

        endpoint.IsListener = true;

        var context = new MessageRoutingContext(messageOrHandlerType, runtime);

        _configureListener(configuration, context);

        configuration!.As<IDelayedEndpointConfiguration>().Apply();

        // When using FromHandlerType naming, the exchange should still be named
        // after the message type so that senders (which always use message type)
        // and listeners share the same exchange. See GH-2397.
        ApplyListenerRoutingDefaults(corrected, transport, originalMessageType ?? messageOrHandlerType);

        return endpoint;
    }

    RoutingConventionDescriptor IMessageRoutingConvention.Describe(IWolverineRuntime runtime)
    {
        var transport = resolveTransport(runtime);
        return new RoutingConventionDescriptor
        {
            Name = GetType().Name,
            Description = "Conventional broker routing: maps each message type to a broker destination (queue/topic/exchange) by naming convention, so messages publish to the broker without an explicit per-type rule.",
            TransportScheme = transport.Protocol,
            TransportName = transport.Name,
            TransportDescription = transport.Describe()
        };
    }

    IEnumerable<Endpoint> IMessageRoutingConvention.DiscoverSenders(Type messageType, IWolverineRuntime runtime)
    {
        var endpoints = tryRegisterSenderConfiguration(messageType, runtime);

        foreach (var endpoint in endpoints)
        {
            // Description passes (FindResources / describe) run before any broker connection
            // exists, and routes built there tolerate a null Sender and are never cached
            // (GH-2897) — so don't force the agent here, where it would open a broker
            // connection during resource discovery or throw (e.g. RabbitMQ's SendingConnection).
            if (WolverineSystemPart.WithinDescription)
            {
                yield return endpoint;
                continue;
            }

            // This will start up the sending agent. Only safe to call once the broker
            // transport has been initialized (i.e. the sending connection is open).
            var sendingAgent = runtime.Endpoints.GetOrBuildSendingAgent(endpoint.Uri);
            yield return sendingAgent.Endpoint;
        }
    }

    void IMessageRoutingConvention.PreregisterSenders(IReadOnlyList<Type> handledMessageTypes, IWolverineRuntime runtime)
    {
        // Eagerly apply subscription metadata and sender configuration for the
        // conventionally-routed sender endpoints derived from this convention's
        // handled message types. This must run BEFORE BrokerTransport.InitializeAsync
        // calls Compile() on the endpoints — otherwise endpoint policies like
        // UseDurableOutboxOnAllSendingEndpoints() that gate on
        // `endpoint.Subscriptions.Any()` won't see the subscription and won't
        // upgrade the endpoint mode to Durable. See GH-2588.
        //
        // CRITICAL: do NOT build the sending agent here — the broker isn't connected
        // yet at this phase of host startup. The agent gets built lazily later when
        // DiscoverSenders runs on the first publish path.
        if (_onlyApplyToInboundMessages)
        {
            return;
        }

        foreach (var messageType in handledMessageTypes)
        {
            tryRegisterSenderConfiguration(messageType, runtime);
        }
    }

    /// <summary>
    /// GH-4524 / GH-4525. The destinations a convention publishes <paramref name="messageType"/> to IN
    /// ADDITION to the one <see cref="FindOrCreateSubscriber"/> names. A transport whose destination fans
    /// out by itself -- a RabbitMQ exchange, an Azure Service Bus topic, an SNS topic -- never needs this:
    /// one destination, and the broker delivers to every queue or subscription behind it. A transport whose
    /// destination is a plain queue has nothing to fan out with, so under
    /// <see cref="MultipleHandlerBehavior.Separated"/> the sender itself has to publish to one queue per
    /// separated handler; <see cref="SeparatedHandlerTypesFor"/> names those handlers. Each endpoint returned
    /// here is registered, configured and routed exactly like the primary one. Empty by default.
    /// </summary>
    protected virtual IEnumerable<(TSubscriber, Endpoint)> FindOrCreateAdditionalSubscribers(string identifier,
        TTransport transport, Type messageType, IWolverineRuntime runtime)
    {
        yield break;
    }

    /// <summary>
    /// GH-4524 / GH-4525. Whether the sender should publish <paramref name="messageType"/> to the message
    /// type's own destination at all. True by default. A queue-only transport under
    /// <see cref="MultipleHandlerBehavior.Separated"/> returns false when every local handler was moved to a
    /// per-handler queue and nothing listens on the message type's queue any more -- publishing there would
    /// pile up copies nobody consumes. The destinations from <see cref="FindOrCreateAdditionalSubscribers"/>
    /// are unaffected.
    /// </summary>
    protected virtual bool ShouldPublishToConventionalDestination(string identifier, TTransport transport,
        Type messageType, IWolverineRuntime runtime)
    {
        return true;
    }

    /// <summary>
    /// True when <see cref="MultipleHandlerBehavior.Separated"/> moved EVERY local handler of
    /// <paramref name="messageType"/> to its own listener, leaving the message type's own destination with no
    /// listener in this process. Mirrors the test <c>DiscoverListeners</c> makes before creating that listener.
    /// </summary>
    protected static bool EveryHandlerIsSeparated(Type messageType, IWolverineRuntime runtime)
    {
        if (runtime.Options.MultipleHandlerBehavior != MultipleHandlerBehavior.Separated)
        {
            return false;
        }

        var chain = runtime.Options.HandlerGraph.ChainFor(messageType);
        return chain != null && !chain.Handlers.Any() && chain.ByEndpoint.Any();
    }

    /// <summary>
    /// The handler types that <see cref="MultipleHandlerBehavior.Separated"/> moved out of the main handler
    /// chain for <paramref name="messageType"/> and gave their own listener -- the same ones
    /// <c>DiscoverListeners</c> hands to <see cref="FindOrCreateListenerForIdentifierUsingSeparatedHandler"/>.
    /// Empty when the behaviour is not Separated, the message has no local handler, or only one handler
    /// handles it (that one keeps the message type's own destination).
    /// </summary>
    protected static IEnumerable<Type> SeparatedHandlerTypesFor(Type messageType, IWolverineRuntime runtime)
    {
        if (runtime.Options.MultipleHandlerBehavior != MultipleHandlerBehavior.Separated)
        {
            return [];
        }

        var chain = runtime.Options.HandlerGraph.ChainFor(messageType);
        if (chain == null)
        {
            return [];
        }

        return chain.ByEndpoint.Select(x => x.Handlers.First().HandlerType).Distinct().ToArray();
    }

    /// <summary>
    /// Locate or create the subscriber endpoint(s) for <paramref name="messageType"/>, register
    /// the subscription and apply <see cref="_configureSending"/> exactly once per message
    /// type. Returns the endpoints -- the primary one first, then any from
    /// <see cref="FindOrCreateAdditionalSubscribers"/> -- or an empty list if filtering rules say this
    /// convention should not produce a sender for the message type. Does NOT build the sending agent —
    /// that is the caller's responsibility (and only safe once the broker is connected).
    /// </summary>
    private IReadOnlyList<Endpoint> tryRegisterSenderConfiguration(Type messageType, IWolverineRuntime runtime)
    {
        if (_onlyApplyToInboundMessages)
        {
            return [];
        }

        if (!_typeFilters.Matches(messageType))
        {
            return [];
        }

        if (messageType.CanBeCastTo<INotToBeRouted>() || messageType == typeof(Envelope))
        {
            return [];
        }

        var destinationName = _identifierForSender(messageType);
        if (destinationName.IsEmpty())
        {
            return [];
        }

        // Serialize the mutation of _configuredSenders and the endpoint's Subscriptions
        // list. This method runs both during startup (PreregisterSenders) and lazily on
        // the first publish (DiscoverSenders); a publish racing the tail of startup would
        // otherwise corrupt the non-thread-safe HashSet. See GH-2874.
        lock (_senderRegistrationLock)
        {
            var transport = resolveTransport(runtime);

            var corrected = transport.MaybeCorrectName(destinationName);
            var firstTime = _configuredSenders.Add(messageType);
            var endpoints = new List<Endpoint>();

            if (ShouldPublishToConventionalDestination(corrected, transport, messageType, runtime))
            {
                var (configuration, endpoint) = FindOrCreateSubscriber(corrected, transport);
                endpoint.EndpointName = destinationName;

                // Register the subscription so that endpoint policies like
                // UseDurableOutboxOnAllSendingEndpoints() recognize this as a sender
                // endpoint when Compile() applies policies. See GH-2304 / GH-2588.
                //
                // Marked IsFromConvention=true so ExplicitRouting (and the diagnostics command)
                // don't mistake the conventional sender for a user-wired publish rule. Without
                // this flag, ExplicitRouting picks up the conventional sender via
                // Endpoint.ShouldSendMessage and short-circuits past LocalRouting — handled
                // messages stop routing to their local handlers and explicit publish rules get
                // duplicated against the conventional broker exchange. See the MessageRoutingTests
                // regression that motivated splitting Subscription.ForType from
                // Subscription.ForConventionalType.
                if (!endpoint.Subscriptions.Any(s => s.Matches(messageType)))
                {
                    endpoint.Subscriptions.Add(Subscription.ForConventionalType(messageType));
                }

                if (firstTime)
                {
                    _configureSending(configuration, new MessageRoutingContext(messageType, runtime));
                    configuration.As<IDelayedEndpointConfiguration>().Apply();
                }

                endpoints.Add(endpoint);
            }

            // GH-4524 / GH-4525: the extra destinations a queue-only transport has to publish to itself
            foreach (var (additionalConfiguration, additional) in
                     FindOrCreateAdditionalSubscribers(corrected, transport, messageType, runtime))
            {
                if (!additional.Subscriptions.Any(s => s.Matches(messageType)))
                {
                    additional.Subscriptions.Add(Subscription.ForConventionalType(messageType));
                }

                if (firstTime)
                {
                    _configureSending(additionalConfiguration, new MessageRoutingContext(messageType, runtime));
                    additionalConfiguration.As<IDelayedEndpointConfiguration>().Apply();
                }

                endpoints.Add(additional);
            }

            return endpoints;
        }
    }

    private bool _onlyApplyToOutboundMessages;

    /// <summary>
    ///     Makes so that the convention only applies to outbound messages, and disables discovery of listeners
    /// </summary>
    public void OnlyApplyToOutboundMessages()
    {
        _onlyApplyToInboundMessages = false;
        _onlyApplyToOutboundMessages = true;
    }

    private bool _onlyApplyToInboundMessages;

    /// <summary>
    ///     Makes so that the convention only applies to inbound messages, and disables discovery of senders
    /// </summary>
    public void OnlyApplyToInboundMessages()
    {
        _onlyApplyToOutboundMessages = false;
        _onlyApplyToInboundMessages = true;
    }

    /// <summary>
    ///     Create an allow list of included message types. This is accumulative.
    /// </summary>
    /// <param name="filter"></param>
    public TSelf IncludeTypes(Func<Type, bool> filter)
    {
        _typeFilters.Includes.Add(filter);
        return this.As<TSelf>();
    }

    /// <summary>
    ///     Create an deny list of included message types. This is accumulative.
    /// </summary>
    /// <param name="filter"></param>
    public TSelf ExcludeTypes(Func<Type, bool> filter)
    {
        _typeFilters.Excludes.Add(filter);
        return this.As<TSelf>();
    }

    protected abstract (TListener, Endpoint) FindOrCreateListenerForIdentifier(string identifier,
        TTransport transport, Type messageType);
    
    protected abstract (TListener, Endpoint) FindOrCreateListenerForIdentifierUsingSeparatedHandler(string identifier,
        TTransport transport, Type messageType, Type handlerType);

    protected abstract (TSubscriber, Endpoint) FindOrCreateSubscriber(string identifier, TTransport transport);

    protected virtual void ApplyListenerRoutingDefaults(string listenerIdentifier, TTransport transport, Type messageType) {}

    /// <summary>
    ///     Control whether conventional routing names queues/topics after the message type (default)
    ///     or the handler type. Using <see cref="NamingSource.FromHandlerType"/> is appropriate for
    ///     modular monolith scenarios where you have more than one handler for a given message type
    ///     and want each handler to receive messages on its own dedicated queue.
    /// </summary>
    /// <param name="source"></param>
    /// <returns></returns>
    public TSelf UseNaming(NamingSource source)
    {
        _namingSource = source;
        return this.As<TSelf>();
    }

    /// <summary>
    ///     Override the convention for determining the queue name for receiving incoming messages of the message type.
    ///     Returning null or empty is interpreted as "don't create a new queue for this message type". Default is the
    ///     MessageTypeName
    /// </summary>
    /// <param name="nameSource"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public TSelf QueueNameForListener(Func<Type, string?> nameSource)
    {
        return IdentifierForListener(nameSource);
    }

    /// <summary>
    ///     Override the convention for determining the queue name for receiving incoming messages of the message type.
    ///     Returning null or empty is interpreted as "don't create a new queue for this message type". Default is the
    ///     MessageTypeName
    /// </summary>
    /// <param name="nameSource"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public TSelf IdentifierForListener(Func<Type, string?> nameSource)
    {
        _queueNameForListener = nameSource ?? throw new ArgumentNullException(nameof(nameSource));
        return this.As<TSelf>();
    }

    /// <summary>
    ///     Override the convention for determining the destination object name that should receive messages of the message
    ///     type.
    ///     Returning null or empty is interpreted as "don't create a new queue for this message type". Default is the
    ///     MessageTypeName
    /// </summary>
    /// <param name="nameSource"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public TSelf IdentifierForSender(Func<Type, string?> nameSource)
    {
        _identifierForSender = nameSource ?? throw new ArgumentNullException(nameof(nameSource));
        return this.As<TSelf>();
    }

    /// <summary>
    ///     Override the Rabbit MQ and Wolverine configuration for new listening endpoints created by message type.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public TSelf ConfigureListeners(Action<TListener, MessageRoutingContext> configure)
    {
        _configureListener = configure ?? throw new ArgumentNullException(nameof(configure));
        return this.As<TSelf>();
    }

    /// <summary>
    ///     Override the Rabbit MQ and Wolverine configuration for sending endpoints, exchanges, and queue bindings
    ///     for a new sending endpoint
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public TSelf ConfigureSending(Action<TSubscriber, MessageRoutingContext> configure)
    {
        _configureSending = configure ?? throw new ArgumentNullException(nameof(configure));
        return this.As<TSelf>();
    }
}