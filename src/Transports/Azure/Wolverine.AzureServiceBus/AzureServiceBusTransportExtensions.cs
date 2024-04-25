using Azure;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using JasperFx.Core.Reflection;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;

namespace Wolverine.AzureServiceBus;

public static class AzureServiceBusTransportExtensions
{
    /// <summary>
    ///     Quick access to the Rabbit MQ Transport within this application.
    ///     This is for advanced usage
    /// </summary>
    /// <param name="endpoints"></param>
    /// <returns></returns>
    internal static AzureServiceBusTransport AzureServiceBusTransport(this WolverineOptions endpoints)
    {
        var transports = endpoints.As<WolverineOptions>().Transports;

        return transports.GetOrCreate<AzureServiceBusTransport>();
    }

    /// <summary>
    /// Additive configuration to the Azure Service Bus integration for this Wolverine application
    /// </summary>
    /// <param name="endpoints"></param>
    /// <returns></returns>
    public static AzureServiceBusConfiguration ConfigureAzureServiceBus(this WolverineOptions endpoints)
    {
        var transport = endpoints.AzureServiceBusTransport();
        return new AzureServiceBusConfiguration(transport, endpoints);
    }

    /// <summary>
    /// Connect to Azure Service Bus with a connection string
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="connectionString"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static AzureServiceBusConfiguration UseAzureServiceBus(this WolverineOptions endpoints,
        string connectionString, Action<ServiceBusClientOptions>? configure = null)
    {
        var transport = endpoints.AzureServiceBusTransport();
        transport.ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        configure?.Invoke(transport.ClientOptions);

        return new AzureServiceBusConfiguration(transport, endpoints);
    }

    /// <summary>
    /// Connect to Azure Service Bus using a namespace and secured through a TokenCredential
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="fullyQualifiedNamespace"></param>
    /// <param name="tokenCredential"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static AzureServiceBusConfiguration UseAzureServiceBus(this WolverineOptions endpoints,
        string fullyQualifiedNamespace, TokenCredential tokenCredential, Action<ServiceBusClientOptions>? configure = null)
    {
        var transport = endpoints.AzureServiceBusTransport();
        transport.FullyQualifiedNamespace = fullyQualifiedNamespace ?? throw new ArgumentNullException(nameof(fullyQualifiedNamespace));
        transport.TokenCredential = tokenCredential ?? throw new ArgumentNullException(nameof(tokenCredential));
        configure?.Invoke(transport.ClientOptions);

        return new AzureServiceBusConfiguration(transport, endpoints);
    }

    /// <summary>
    /// Connect to Azure Service Bus using a namespace and secured through an AzureNamedKeyCredential
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="fullyQualifiedNamespace"></param>
    /// <param name="namedKeyCredential"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static AzureServiceBusConfiguration UseAzureServiceBus(this WolverineOptions endpoints,
        string fullyQualifiedNamespace, AzureNamedKeyCredential namedKeyCredential, Action<ServiceBusClientOptions>? configure = null)
    {
        var transport = endpoints.AzureServiceBusTransport();
        transport.FullyQualifiedNamespace = fullyQualifiedNamespace ?? throw new ArgumentNullException(nameof(fullyQualifiedNamespace));
        transport.NamedKeyCredential = namedKeyCredential ?? throw new ArgumentNullException(nameof(namedKeyCredential));
        configure?.Invoke(transport.ClientOptions);

        return new AzureServiceBusConfiguration(transport, endpoints);
    }

    /// <summary>
    /// Connect to Azure Service Bus using a namespace and secured through an AzureSasCredential
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="fullyQualifiedNamespace"></param>
    /// <param name="sasCredential"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static AzureServiceBusConfiguration UseAzureServiceBus(this WolverineOptions endpoints,
        string fullyQualifiedNamespace, AzureSasCredential sasCredential, Action<ServiceBusClientOptions>? configure = null)
    {
        var transport = endpoints.AzureServiceBusTransport();
        transport.FullyQualifiedNamespace = fullyQualifiedNamespace ?? throw new ArgumentNullException(nameof(fullyQualifiedNamespace));
        transport.SasCredential = sasCredential ?? throw new ArgumentNullException(nameof(sasCredential));
        configure?.Invoke(transport.ClientOptions);

        return new AzureServiceBusConfiguration(transport, endpoints);
    }

    /// <summary>
    ///     Listen for incoming messages at the designated Rabbit MQ queue by name
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="queueName">The name of the Rabbit MQ queue</param>
    /// <param name="configure">
    ///     Optional configuration for this Rabbit Mq queue if being initialized by Wolverine
    ///     <returns></returns>
    public static AzureServiceBusQueueListenerConfiguration ListenToAzureServiceBusQueue(
        this WolverineOptions endpoints, string queueName, Action<AzureServiceBusQueue>? configure = null)
    {
        var transport = endpoints.AzureServiceBusTransport();

        var corrected = transport.MaybeCorrectName(queueName);
        var endpoint = transport.Queues[corrected];
        endpoint.EndpointName = queueName;
        endpoint.IsListener = true;

        configure?.Invoke(endpoint);

        return new AzureServiceBusQueueListenerConfiguration(endpoint);
    }

    public class SubscriptionExpression
    {
        private readonly string _subscriptionName;
        private readonly Action<CreateSubscriptionOptions>? _configureSubscriptions;
        private readonly Action<CreateRuleOptions>? _configureSubscriptionRule;
        private readonly AzureServiceBusTransport _transport;

        public SubscriptionExpression(
            string subscriptionName, 
            Action<CreateSubscriptionOptions>? configureSubscriptions, 
            Action<CreateRuleOptions>? configureSubscriptionRule, 
            AzureServiceBusTransport transport)
        {
            _subscriptionName = subscriptionName;
            _configureSubscriptions = configureSubscriptions;
            _configureSubscriptionRule = configureSubscriptionRule;
            _transport = transport;
        }

        public AzureServiceBusSubscriptionListenerConfiguration FromTopic(string topicName, Action<CreateTopicOptions>? configureTopic = null)
        {
            if (topicName == null)
            {
                throw new ArgumentNullException(nameof(topicName));
            }

            // Gather any naming prefix
            topicName = _transport.MaybeCorrectName(topicName);

            var topic = _transport.Topics[topicName];
            configureTopic?.Invoke(topic.Options);
            
            var subscription = topic.FindOrCreateSubscription(_subscriptionName);
            subscription.IsListener = true;
        
            _configureSubscriptions?.Invoke(subscription.Options);
            _configureSubscriptionRule?.Invoke(subscription.RuleOptions);

            return new AzureServiceBusSubscriptionListenerConfiguration(subscription);
        }
    }

    /// <summary>
    /// Listen for messages from an Azure Service Bus topic subscription
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="subscriptionName"></param>
    /// <param name="topicName"></param>
    /// <param name="configureTopic">Optionally apply customizations to the Azure Service Bus topic</param>
    /// <param name="configureSubscriptions">Optionally apply customizations to the actual Azure Service Bus subscription</param>
    /// <param name="configureSubscriptionRule">Optionally apply customizations to the Azure Service Bus subscription rule</param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static SubscriptionExpression ListenToAzureServiceBusSubscription(
        this WolverineOptions endpoints, 
        string subscriptionName, 
        Action<CreateSubscriptionOptions>? configureSubscriptions = null,
        Action<CreateRuleOptions>? configureSubscriptionRule = null)
    {
        if (subscriptionName == null)
        {
            throw new ArgumentNullException(nameof(subscriptionName));
        }

        var transport = endpoints.AzureServiceBusTransport();

        return new SubscriptionExpression(
            transport.MaybeCorrectName(subscriptionName), 
            configureSubscriptions,
            configureSubscriptionRule,
            transport);
    }

    /// <summary>
    /// Publish the designated messages directly to an Azure Service Bus queue
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="queueName"></param>
    /// <returns></returns>
    public static AzureServiceBusQueueSubscriberConfiguration ToAzureServiceBusQueue(
        this IPublishToExpression publishing, string queueName)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<AzureServiceBusTransport>();

        var corrected = transport.MaybeCorrectName(queueName);

        var endpoint = transport.Queues[corrected];
        endpoint.EndpointName = queueName;

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(endpoint.Uri);

        return new AzureServiceBusQueueSubscriberConfiguration(endpoint);
    }
    
    /// <summary>
    /// Publish the designated messages to an Azure Service Bus topic
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="topicName"></param>
    /// <returns></returns>
    public static AzureServiceBusTopicSubscriberConfiguration ToAzureServiceBusTopic(
        this IPublishToExpression publishing, string topicName)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<AzureServiceBusTransport>();

        var corrected = transport.MaybeCorrectName(topicName);

        var endpoint = transport.Topics[corrected];
        endpoint.EndpointName = topicName;

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(endpoint.Uri);

        return new AzureServiceBusTopicSubscriberConfiguration(endpoint);
    }
    
    
}