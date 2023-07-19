using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Util;
using Endpoint = Wolverine.Configuration.Endpoint;

namespace Wolverine.AzureServiceBus;

public class AzureServiceBusQueueAndTopicMessageRoutingConvention : IMessageRoutingConvention
{
    /// <summary>
    ///     Optionally include (allow list) or exclude (deny list) types. By default, this will apply to all message types
    /// </summary>
    private readonly CompositeFilter<Type> typeFilters = new();

    private Action<AzureServiceBusQueueListenerConfiguration, MessageRoutingContext> configureQueueListener = (_, _) => { };
    private Action<AzureServiceBusQueueSubscriberConfiguration, MessageRoutingContext> configureQueueSending = (_, _) => { };

    private Action<AzureServiceBusSubscriptionListenerConfiguration, MessageRoutingContext> configureSubscriptionListener = (_, _) => { };
    private Action<AzureServiceBusTopicSubscriberConfiguration, MessageRoutingContext> configureTopicSending = (_, _) => { };

    private Func<Type, string?> identifierForListener = t => t.ToMessageTypeName();
    private Func<Type, string?> identifierForSubscription = t => t.ToMessageTypeName();
    private Func<Type, string?> identifierForSender = t => t.ToMessageTypeName();
    private Func<Type, bool> broadcastedTypesSelector = (_) => false;

    void IMessageRoutingConvention.DiscoverListeners(IWolverineRuntime runtime, IReadOnlyList<Type> handledMessageTypes)
    {
        AzureServiceBusTransport transport = runtime.Options.Transports.GetOrCreate<AzureServiceBusTransport>();

        foreach (Type messageType in handledMessageTypes.Where(typeFilters.Matches))
        {
            // Can be null, so bail out if there's no queue
            string? name = identifierForListener(messageType);
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
        if (!typeFilters.Matches(messageType))
        {
            yield break;
        }

        AzureServiceBusTransport transport = runtime.Options.Transports.GetOrCreate<AzureServiceBusTransport>();

        var destinationName = identifierForSender(messageType);
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
        // Broadcasted Types use Topics
        if (broadcastedTypesSelector(context.MessageType))
        {
            AzureServiceBusTopic topic = transport.Topics[identifier];

            AzureServiceBusTopicSubscriberConfiguration topicConfiguration = new AzureServiceBusTopicSubscriberConfiguration(topic);
            configureTopicSending(topicConfiguration, context);

            return (topicConfiguration, topic);
        }

        var queue = transport.Queues[identifier];
        AzureServiceBusQueueSubscriberConfiguration queueConfiguration = new AzureServiceBusQueueSubscriberConfiguration(queue);
        configureQueueSending(queueConfiguration, context);

        return (queueConfiguration, queue);
    }

    protected (IDelayedEndpointConfiguration, Endpoint) FindOrCreateListenerForIdentifier(string identifier, MessageRoutingContext context, AzureServiceBusTransport transport)
    {
        // Broadcasted Types use Topics
        if (broadcastedTypesSelector(context.MessageType))
        {
            string? subscriptionName = identifierForSubscription(context.MessageType);

            AzureServiceBusTopic topic = transport.Topics[identifier];
            AzureServiceBusSubscription subscription = topic.FindOrCreateSubscription(subscriptionName!);

            AzureServiceBusSubscriptionListenerConfiguration subscriptionConfiguration = new AzureServiceBusSubscriptionListenerConfiguration(subscription);

            configureSubscriptionListener(subscriptionConfiguration, context);
            return (subscriptionConfiguration, subscription);
        }

        AzureServiceBusQueue queue = transport.Queues[identifier];
        AzureServiceBusQueueListenerConfiguration queueConfiguration = new AzureServiceBusQueueListenerConfiguration(queue);

        configureQueueListener(queueConfiguration, context);
        return (queueConfiguration, queue);
    }

    /// <summary>
    /// Create an allow list of included message types. This is accumulative.
    /// </summary>
    /// <param name="filter"></param>
    public AzureServiceBusQueueAndTopicMessageRoutingConvention IncludeTypes(Func<Type, bool> filter)
    {
        typeFilters.Includes.Add(filter);
        return this;
    }

    /// <summary>
    /// Create an deny list of included message types. This is accumulative.
    /// </summary>
    /// <param name="filter"></param>
    public AzureServiceBusQueueAndTopicMessageRoutingConvention ExcludeTypes(Func<Type, bool> filter)
    {
        typeFilters.Excludes.Add(filter);
        return this;
    }

    /// <summary>
    /// Override the convention for determining the queue name for receiving incoming messages of the message type.
    /// Returning null or empty is interpreted as "don't create a new queue for this message type". Default is the MessageTypeName
    /// </summary>
    /// <param name="nameSource"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public AzureServiceBusQueueAndTopicMessageRoutingConvention IdentifierForListener(Func<Type, string?> nameSource)
    {
        identifierForListener = nameSource ?? throw new ArgumentNullException(nameof(nameSource));
        return this;
    }

    /// <summary>
    /// Override the convention for determining the destination object name that should receive messages of the message type.
    /// Returning null or empty is interpreted as "don't create a new queue for this message type". Default is the MessageTypeName
    /// </summary>
    /// <param name="nameSource"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public AzureServiceBusQueueAndTopicMessageRoutingConvention IdentifierForSender(Func<Type, string?> nameSource)
    {
        identifierForSender = nameSource ?? throw new ArgumentNullException(nameof(nameSource));
        return this;
    }

    /// <summary>
    /// Override the Rabbit MQ and Wolverine configuration for new listening endpoints created by message type.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AzureServiceBusQueueAndTopicMessageRoutingConvention ConfigureQueueListeners(Action<AzureServiceBusQueueListenerConfiguration, MessageRoutingContext> configure)
    {
        configureQueueListener = configure ?? throw new ArgumentNullException(nameof(configure));
        return this;
    }

    /// <summary>
    /// Override the Rabbit MQ and Wolverine configuration for sending endpoints, exchanges, and queue bindings
    /// for a new sending endpoint
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AzureServiceBusQueueAndTopicMessageRoutingConvention ConfigureTopicSending(Action<AzureServiceBusTopicSubscriberConfiguration, MessageRoutingContext> configure)
    {
        configureTopicSending = configure ?? throw new ArgumentNullException(nameof(configure));
        return this;
    }

    /// <summary>
    /// Override the Rabbit MQ and Wolverine configuration for new listening endpoints created by message type.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AzureServiceBusQueueAndTopicMessageRoutingConvention ConfigureSubscriptionListeners(Action<AzureServiceBusSubscriptionListenerConfiguration, MessageRoutingContext> configure)
    {
        configureSubscriptionListener = configure ?? throw new ArgumentNullException(nameof(configure));
        return this;
    }

    /// <summary>
    /// Override the Rabbit MQ and Wolverine configuration for sending endpoints, exchanges, and queue bindings
    /// for a new sending endpoint
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AzureServiceBusQueueAndTopicMessageRoutingConvention ConfigureQueueSending(Action<AzureServiceBusQueueSubscriberConfiguration, MessageRoutingContext> configure)
    {
        configureQueueSending = configure ?? throw new ArgumentNullException(nameof(configure));
        return this;
    }

    /// <summary>
    ///     Specify naming rules for the subscribing queue for message types
    /// </summary>
    /// <param name="namingRule"></param>
    /// <returns></returns>
    public AzureServiceBusQueueAndTopicMessageRoutingConvention UsePublishingBroadcastFor(Func<Type, bool> selector, Func<Type, string?> topicSubscriberNameSource)
    {
        this.broadcastedTypesSelector = selector ?? throw new ArgumentNullException(nameof(selector));
        this.identifierForSubscription = topicSubscriberNameSource ?? throw new ArgumentNullException(nameof(topicSubscriberNameSource));
        return this;
    }
}