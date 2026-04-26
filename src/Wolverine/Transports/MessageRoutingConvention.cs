using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Runtime.Routing;
using Wolverine.Util;

namespace Wolverine.Transports;

public abstract class MessageRoutingConvention<TTransport, TListener, TSubscriber, TSelf> : IMessageRoutingConvention
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

    void IMessageRoutingConvention.DiscoverListeners(IWolverineRuntime runtime, IReadOnlyList<Type> handledMessageTypes)
    {
        if(_onlyApplyToOutboundMessages)
        {
            return;
        }

        var transport = runtime.Options.Transports.GetOrCreate<TTransport>();

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

            if (_namingSource == NamingSource.FromHandlerType && chain.Handlers.Any())
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

    IEnumerable<Endpoint> IMessageRoutingConvention.DiscoverSenders(Type messageType, IWolverineRuntime runtime)
    {
        var endpoint = tryRegisterSenderConfiguration(messageType, runtime);
        if (endpoint == null)
        {
            yield break;
        }

        // This will start up the sending agent. Only safe to call once the broker
        // transport has been initialized (i.e. the sending connection is open).
        var sendingAgent = runtime.Endpoints.GetOrBuildSendingAgent(endpoint.Uri);
        yield return sendingAgent.Endpoint;
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
    /// Locate or create the subscriber endpoint for <paramref name="messageType"/>, register
    /// the subscription and apply <see cref="_configureSending"/> exactly once per message
    /// type. Returns the endpoint, or null if filtering rules say this convention should
    /// not produce a sender for the message type. Does NOT build the sending agent — that
    /// is the caller's responsibility (and only safe once the broker is connected).
    /// </summary>
    private Endpoint? tryRegisterSenderConfiguration(Type messageType, IWolverineRuntime runtime)
    {
        if (_onlyApplyToInboundMessages)
        {
            return null;
        }

        if (!_typeFilters.Matches(messageType))
        {
            return null;
        }

        if (messageType.CanBeCastTo<INotToBeRouted>() || messageType == typeof(Envelope))
        {
            return null;
        }

        var transport = runtime.Options.Transports.GetOrCreate<TTransport>();

        var destinationName = _identifierForSender(messageType);
        if (destinationName.IsEmpty())
        {
            return null;
        }

        var corrected = transport.MaybeCorrectName(destinationName);

        var (configuration, endpoint) = FindOrCreateSubscriber(corrected, transport);
        endpoint.EndpointName = destinationName;

        // Register the subscription so that endpoint policies like
        // UseDurableOutboxOnAllSendingEndpoints() recognize this as a sender
        // endpoint when Compile() applies policies. See GH-2304 / GH-2588.
        if (!endpoint.Subscriptions.Any(s => s.Matches(messageType)))
        {
            endpoint.Subscriptions.Add(Subscription.ForType(messageType));
        }

        if (_configuredSenders.Add(messageType))
        {
            _configureSending(configuration, new MessageRoutingContext(messageType, runtime));
            configuration.As<IDelayedEndpointConfiguration>().Apply();
        }

        return endpoint;
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