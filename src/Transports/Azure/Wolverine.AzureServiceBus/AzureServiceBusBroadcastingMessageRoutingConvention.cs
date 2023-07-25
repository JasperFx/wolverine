using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Util;
using Endpoint = Wolverine.Configuration.Endpoint;

namespace Wolverine.AzureServiceBus;

public class AzureServiceBusBroadcastingMessageRoutingConvention : IMessageRoutingConvention
{
    /// <summary>
    ///     Optionally include (allow list) or exclude (deny list) types. By default, this will apply to all message types
    /// </summary>
    private readonly CompositeFilter<Type> _typeFilters = new();

    private Action<AzureServiceBusSubscriptionListenerConfiguration, MessageRoutingContext> _configureSubscriptionListener = (_, _) => { };
    private Action<AzureServiceBusTopicSubscriberConfiguration, MessageRoutingContext> _configureTopicSending = (_, _) => { };

    private Func<Type, string?> _identifierForListener = t => t.ToMessageTypeName();
    private Func<Type, string?> _identifierForSubscription = t => t.ToMessageTypeName();
    private Func<Type, string?> _identifierForSender = t => t.ToMessageTypeName();

    void IMessageRoutingConvention.DiscoverListeners(IWolverineRuntime runtime, IReadOnlyList<Type> handledMessageTypes)
    {
        AzureServiceBusTransport transport = runtime.Options.Transports.GetOrCreate<AzureServiceBusTransport>();

        foreach (Type messageType in handledMessageTypes.Where(_typeFilters.Matches))
        {
            // Can be null, so bail out if there's no queue
            string? name = _identifierForListener(messageType);
            if (name.IsEmpty())
                return;

            string correctedIdentifier = transport.MaybeCorrectName(name);
            MessageRoutingContext context = new MessageRoutingContext(messageType, runtime);

            var (configuration, endpoint) = FindOrCreateListenerForIdentifier(correctedIdentifier, context, transport);

            endpoint.EndpointName = name;
            endpoint.IsListener = true;

            configuration!.As<IDelayedEndpointConfiguration>().Apply();
        }
    }

    IEnumerable<Endpoint> IMessageRoutingConvention.DiscoverSenders(Type messageType, IWolverineRuntime runtime)
    {
        if (!_typeFilters.Matches(messageType))
        {
            yield break;
        }

        AzureServiceBusTransport transport = runtime.Options.Transports.GetOrCreate<AzureServiceBusTransport>();

        var destinationName = _identifierForSender(messageType);
        if (destinationName.IsEmpty())
        {
            yield break;
        }

        var correctedIdentifier = transport.MaybeCorrectName(destinationName);
        MessageRoutingContext context = new MessageRoutingContext(messageType, runtime);

        var (configuration, endpoint) = FindOrCreateSubscriber(correctedIdentifier, context, transport);
        endpoint.EndpointName = destinationName;

        configuration.As<IDelayedEndpointConfiguration>().Apply();

        // This will start up the sending agent
        var sendingAgent = runtime.Endpoints.GetOrBuildSendingAgent(endpoint.Uri);
        yield return sendingAgent.Endpoint;
    }

    protected (IDelayedEndpointConfiguration, Endpoint) FindOrCreateSubscriber(string identifier, MessageRoutingContext context, AzureServiceBusTransport transport)
    {
        AzureServiceBusTopic topic = transport.Topics[identifier];

        AzureServiceBusTopicSubscriberConfiguration topicConfiguration = new AzureServiceBusTopicSubscriberConfiguration(topic);
        _configureTopicSending(topicConfiguration, context);

        return (topicConfiguration, topic);
    }

    protected (IDelayedEndpointConfiguration, Endpoint) FindOrCreateListenerForIdentifier(string identifier, MessageRoutingContext context, AzureServiceBusTransport transport)
    {
        string? subscriptionName = _identifierForSubscription(context.MessageType);

        AzureServiceBusTopic topic = transport.Topics[identifier];
        AzureServiceBusSubscription subscription = topic.FindOrCreateSubscription(subscriptionName!);

        AzureServiceBusSubscriptionListenerConfiguration subscriptionConfiguration = new AzureServiceBusSubscriptionListenerConfiguration(subscription);

        _configureSubscriptionListener(subscriptionConfiguration, context);
        return (subscriptionConfiguration, subscription);
    }

    /// <summary>
    /// Create an allow list of included message types. This is accumulative.
    /// </summary>
    /// <param name="filter"></param>
    public AzureServiceBusBroadcastingMessageRoutingConvention IncludeTypes(Func<Type, bool> filter)
    {
        _typeFilters.Includes.Add(filter);
        return this;
    }

    /// <summary>
    /// Create an deny list of included message types. This is accumulative.
    /// </summary>
    /// <param name="filter"></param>
    public AzureServiceBusBroadcastingMessageRoutingConvention ExcludeTypes(Func<Type, bool> filter)
    {
        _typeFilters.Excludes.Add(filter);
        return this;
    }

    /// <summary>
    /// Override the convention for determining the queue name for receiving incoming messages of the message type.
    /// Returning null or empty is interpreted as "don't create a new queue for this message type". Default is the MessageTypeName
    /// </summary>
    /// <param name="nameSource"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public AzureServiceBusBroadcastingMessageRoutingConvention TopicNameForListener(Func<Type, string?> nameSource)
    {
        _identifierForListener = nameSource ?? throw new ArgumentNullException(nameof(nameSource));
        return this;
    }

    /// <summary>
    /// Override the convention for determining the destination object name that should receive messages of the message type.
    /// Returning null or empty is interpreted as "don't create a new queue for this message type". Default is the MessageTypeName
    /// </summary>
    /// <param name="nameSource"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public AzureServiceBusBroadcastingMessageRoutingConvention TopicNameForSender(Func<Type, string?> nameSource)
    {
        _identifierForSender = nameSource ?? throw new ArgumentNullException(nameof(nameSource));
        return this;
    }

    /// <summary>
    /// Override the convention for determining the destination object name that should receive messages of the message type.
    /// Returning null or empty is interpreted as "don't create a new queue for this message type". Default is the MessageTypeName
    /// </summary>
    /// <param name="nameSource"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public AzureServiceBusBroadcastingMessageRoutingConvention SubscriptionNameForListener(Func<Type, string?> nameSource)
    {
        this._identifierForSubscription = nameSource ?? throw new ArgumentNullException(nameof(nameSource));
        return this;
    }

    /// <summary>
    /// Override the Rabbit MQ and Wolverine configuration for sending endpoints, exchanges, and queue bindings
    /// for a new sending endpoint
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AzureServiceBusBroadcastingMessageRoutingConvention ConfigureTopicSending(Action<AzureServiceBusTopicSubscriberConfiguration, MessageRoutingContext> configure)
    {
        _configureTopicSending = configure ?? throw new ArgumentNullException(nameof(configure));
        return this;
    }

    /// <summary>
    /// Override the Rabbit MQ and Wolverine configuration for new listening endpoints created by message type.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AzureServiceBusBroadcastingMessageRoutingConvention ConfigureSubscriptionListeners(Action<AzureServiceBusSubscriptionListenerConfiguration, MessageRoutingContext> configure)
    {
        _configureSubscriptionListener = configure ?? throw new ArgumentNullException(nameof(configure));
        return this;
    }
}