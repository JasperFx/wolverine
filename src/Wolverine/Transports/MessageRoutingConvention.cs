using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime;
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
    private Func<Type, string?> _identifierForSender = t => t.ToMessageTypeName();
    private Func<Type, string?> _queueNameForListener = t => t.ToMessageTypeName();

    void IMessageRoutingConvention.DiscoverListeners(IWolverineRuntime runtime, IReadOnlyList<Type> handledMessageTypes)
    {
        if(_onlyApplyToOutboundMessages)
        {
            return;
        }

        var transport = runtime.Options.Transports.GetOrCreate<TTransport>();

        foreach (var messageType in handledMessageTypes.Where(t => _typeFilters.Matches(t)))
        {
            // Can be null, so bail out if there's no queue
            var queueName = _queueNameForListener(messageType);
            if (queueName.IsEmpty())
            {
                return;
            }

            var corrected = transport.MaybeCorrectName(queueName);

            var (configuration, endpoint) = findOrCreateListenerForIdentifier(corrected, transport, messageType);
            endpoint.EndpointName = queueName;

            endpoint.IsListener = true;

            var context = new MessageRoutingContext(messageType, runtime);

            _configureListener(configuration, context);

            configuration!.As<IDelayedEndpointConfiguration>().Apply();
        }
    }

    IEnumerable<Endpoint> IMessageRoutingConvention.DiscoverSenders(Type messageType, IWolverineRuntime runtime)
    {
        if(_onlyApplyToInboundMessages)
        {
            yield break;
        }

        if (!_typeFilters.Matches(messageType))
        {
            yield break;
        }

        var transport = runtime.Options.Transports.GetOrCreate<TTransport>();

        var destinationName = _identifierForSender(messageType);
        if (destinationName.IsEmpty())
        {
            yield break;
        }

        var corrected = transport.MaybeCorrectName(destinationName);

        var (configuration, endpoint) = findOrCreateSubscriber(corrected, transport);
        endpoint.EndpointName = destinationName;

        _configureSending(configuration, new MessageRoutingContext(messageType, runtime));

        configuration.As<IDelayedEndpointConfiguration>().Apply();

        // This will start up the sending agent
        var sendingAgent = runtime.Endpoints.GetOrBuildSendingAgent(endpoint.Uri);
        yield return sendingAgent.Endpoint;
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

    protected abstract (TListener, Endpoint) findOrCreateListenerForIdentifier(string identifier,
        TTransport transport, Type messageType);

    protected abstract (TSubscriber, Endpoint) findOrCreateSubscriber(string identifier, TTransport transport);

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