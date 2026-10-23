using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Partitioning;

namespace Wolverine.AzureServiceBus;

public static class AzureServiceBusTransportExtensions
{
    /// <summary>
    /// CAUTION!!!! This will try to delete all configured objects in the Azure Service Bus namespace.
    /// This is probably only useful for testing
    /// </summary>
    /// <param name="host"></param>
    public static async Task DeleteAllAzureServiceBusObjectsAsync(this IHost host)
    {
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.AzureServiceBusTransport();
        await transport.DeleteAllObjectsAsync();
    }
    
    /// <summary>
    ///     Quick access to the Rabbit MQ Transport within this application.
    ///     This is for advanced usage
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="brokerName"></param>
    /// <returns></returns>
    internal static AzureServiceBusTransport AzureServiceBusTransport(this WolverineOptions endpoints, BrokerName? brokerName = null)
    {
        TransportCollection transports = endpoints.As<WolverineOptions>().Transports;

        return transports.GetOrCreate<AzureServiceBusTransport>(brokerName);
    }

    /// <summary>
    /// Additive configuration to the Azure Service Bus integration for this Wolverine application
    /// </summary>
    /// <param name="endpoints"></param>
    /// <returns></returns>
    public static AzureServiceBusConfiguration ConfigureAzureServiceBus(this WolverineOptions endpoints)
    {
        AzureServiceBusTransport transport = endpoints.AzureServiceBusTransport();
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
        AzureServiceBusTransport transport = endpoints.AzureServiceBusTransport();
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
        AzureServiceBusTransport transport = endpoints.AzureServiceBusTransport();
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
        AzureServiceBusTransport transport = endpoints.AzureServiceBusTransport();
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
        AzureServiceBusTransport transport = endpoints.AzureServiceBusTransport();
        transport.FullyQualifiedNamespace = fullyQualifiedNamespace ?? throw new ArgumentNullException(nameof(fullyQualifiedNamespace));
        transport.SasCredential = sasCredential ?? throw new ArgumentNullException(nameof(sasCredential));
        configure?.Invoke(transport.ClientOptions);

        return new AzureServiceBusConfiguration(transport, endpoints);
    }

    /// <summary>
    /// Direct Wolverine to connect to Azure Service Bus using the named connection string from IConfiguration.
    /// This option is useful for Aspire integration between Wolverine and Aspire, where the AppHost's
    /// <c>builder.AddAzureServiceBus("name")</c> plus <c>.WithReference(...)</c> supplies <c>ConnectionStrings:name</c>.
    ///
    /// <para>
    /// The value is resolved once the application starts, and either of the two shapes Aspire supplies is accepted:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// A full connection string -- anything with an <c>Endpoint=</c> segment and a key, which is what Aspire hands out
    /// for the Azure Service Bus emulator (<c>Endpoint=sb://...;SharedAccessKeyName=...;SharedAccessKey=...;UseDevelopmentEmulator=true;</c>).
    /// It is used as the connection string, exactly as <c>UseAzureServiceBus(connectionString)</c> would.
    /// </item>
    /// <item>
    /// Just the namespace -- what Aspire hands out for a real Azure namespace, the namespace's service bus endpoint
    /// (e.g. <c>https://myns.servicebus.windows.net:443/</c>), or a bare <c>myns.servicebus.windows.net</c>, or an
    /// <c>Endpoint=</c> segment with no key. The host name becomes the fully qualified namespace, secured through
    /// <paramref name="tokenCredential"/>, or a <c>DefaultAzureCredential</c> (managed identity, environment, Azure CLI
    /// and so on) when that is null.
    /// </item>
    /// </list>
    ///
    /// <para>
    /// An <c>EntityPath</c> segment (what Aspire adds when you reference a single queue or topic resource rather than
    /// the namespace) is dropped, because Wolverine addresses every queue and topic through one client.
    /// </para>
    ///
    /// <para>
    /// A missing connection string fails application startup, together with every other missing named connection
    /// string, naming the Aspire <c>AddAzureServiceBus</c> remedy.
    /// </para>
    ///
    /// <para>
    /// The Azure Service Bus emulator serves its management (HTTP) API on a different port than messaging, and
    /// Aspire's emulator connection string only names the messaging port. If you want Wolverine to provision queues and
    /// topics against the emulator (<c>AutoProvision()</c>), also set <see cref="AzureServiceBusTransport.ManagementConnectionString"/>;
    /// otherwise declare the entities in the AppHost.
    /// </para>
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="connectionStringName">Key of the expected connection string from IConfiguration</param>
    /// <param name="configure">Optional extra configuration of the ServiceBusClientOptions</param>
    /// <param name="tokenCredential">
    /// The credential used when the configured value is a namespace rather than a full connection string. Defaults
    /// to a <c>DefaultAzureCredential</c>. Ignored when the value is a connection string.
    /// </param>
    /// <returns></returns>
    public static AzureServiceBusConfiguration UseAzureServiceBusUsingNamedConnection(this WolverineOptions endpoints,
        string connectionStringName, Action<ServiceBusClientOptions>? configure = null,
        TokenCredential? tokenCredential = null)
    {
        ArgumentNullException.ThrowIfNull(connectionStringName);

        var transport = endpoints.AzureServiceBusTransport();
        return useNamedConnection(endpoints, transport, connectionStringName, configure, tokenCredential);
    }

    /// <summary>
    /// Add a named Azure Service Bus broker connected through the named connection string from IConfiguration.
    /// This option is useful for Aspire integration between Wolverine and Aspire when an application talks to more
    /// than one Azure Service Bus namespace. The connection string value is interpreted exactly as it is by
    /// <see cref="UseAzureServiceBusUsingNamedConnection"/>: a full connection string, or a namespace secured through
    /// <paramref name="tokenCredential"/> (a <c>DefaultAzureCredential</c> when null).
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="brokerName"></param>
    /// <param name="connectionStringName">Key of the expected connection string from IConfiguration</param>
    /// <param name="configure">Optional extra configuration of the ServiceBusClientOptions</param>
    /// <param name="tokenCredential">
    /// The credential used when the configured value is a namespace rather than a full connection string. Defaults
    /// to a <c>DefaultAzureCredential</c>. Ignored when the value is a connection string.
    /// </param>
    /// <returns></returns>
    public static AzureServiceBusConfiguration AddNamedAzureServiceBusBrokerUsingNamedConnection(
        this WolverineOptions endpoints, BrokerName brokerName, string connectionStringName,
        Action<ServiceBusClientOptions>? configure = null, TokenCredential? tokenCredential = null)
    {
        ArgumentNullException.ThrowIfNull(brokerName);
        ArgumentNullException.ThrowIfNull(connectionStringName);

        var transport = endpoints.AzureServiceBusTransport(brokerName);
        return useNamedConnection(endpoints, transport, connectionStringName, configure, tokenCredential);
    }

    private static AzureServiceBusConfiguration useNamedConnection(WolverineOptions endpoints,
        AzureServiceBusTransport transport, string connectionStringName, Action<ServiceBusClientOptions>? configure,
        TokenCredential? tokenCredential)
    {
        // The clients are built lazily on first use (connect, provision, or the resources commands), all of which
        // come after the core hook applies the value, so only the connection information waits for IConfiguration.
        configure?.Invoke(transport.ClientOptions);

        endpoints.ApplyNamedConnectionString("Azure Service Bus", connectionStringName, "AddAzureServiceBus",
            value => ApplyNamedConnectionValue(transport, value, tokenCredential));

        return new AzureServiceBusConfiguration(transport, endpoints);
    }

    /// <summary>
    /// Push a connection string value resolved by name (GH-5126) into the transport: a full connection string goes to
    /// <see cref="AzureServiceBusTransport.ConnectionString"/>, a namespace (bare, as a URI, or as a key-less
    /// <c>Endpoint=</c> segment) to <see cref="AzureServiceBusTransport.FullyQualifiedNamespace"/> plus a TokenCredential.
    /// </summary>
    internal static void ApplyNamedConnectionValue(AzureServiceBusTransport transport, string value,
        TokenCredential? tokenCredential)
    {
        var segments = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string? endpoint = null;
        var hasEntityPath = false;
        var kept = new List<string>();
        foreach (var segment in segments)
        {
            // Only split on the FIRST '=' -- SharedAccessKey values are base64 and carry '=' padding
            var pair = segment.Split('=', 2);
            var key = pair[0].Trim();

            if (pair.Length == 2 && key.EqualsIgnoreCase("EntityPath"))
            {
                hasEntityPath = true;
                continue;
            }

            if (pair.Length == 2 && key.EqualsIgnoreCase("Endpoint"))
            {
                endpoint = pair[1].Trim();
            }

            kept.Add(segment);
        }

        if (endpoint == null)
        {
            // No Endpoint= at all: Aspire's value for a real Azure namespace
            useNamespace(transport, value.Trim(), tokenCredential);
            return;
        }

        if (kept.Count == 1)
        {
            // Endpoint= and nothing else to authenticate with (Aspire's form when a queue or topic is referenced)
            useNamespace(transport, endpoint, tokenCredential);
            return;
        }

        transport.ConnectionString = hasEntityPath ? string.Join(';', kept) + ";" : value;
    }

    private static void useNamespace(AzureServiceBusTransport transport, string value, TokenCredential? tokenCredential)
    {
        // "https://myns.servicebus.windows.net:443/" or "sb://myns.servicebus.windows.net/" -> the host name. A bare
        // "myns.servicebus.windows.net" is not an absolute URI; "localhost:5672" parses as one with no host.
        var fullyQualifiedNamespace = Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Host.IsNotEmpty()
            ? uri.Host
            : value.TrimEnd('/');

        transport.FullyQualifiedNamespace = fullyQualifiedNamespace;
        transport.TokenCredential = tokenCredential ?? new DefaultAzureCredential();
    }


    /// <summary>
    /// Connect to Azure Service Bus with a connection string
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="brokerName"></param>
    /// <param name="connectionString"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static AzureServiceBusConfiguration AddNamedAzureServiceBusBroker(this WolverineOptions endpoints,
        BrokerName brokerName,
        string connectionString, Action<ServiceBusClientOptions>? configure = null)
    {
        AzureServiceBusTransport transport = endpoints.AzureServiceBusTransport(brokerName);
        transport.ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        configure?.Invoke(transport.ClientOptions);

        return new AzureServiceBusConfiguration(transport, endpoints);
    }

    /// <summary>
    /// Connect to Azure Service Bus using a namespace and secured through a TokenCredential
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="brokerName"></param>
    /// <param name="fullyQualifiedNamespace"></param>
    /// <param name="tokenCredential"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static AzureServiceBusConfiguration AddNamedAzureServiceBusBroker(this WolverineOptions endpoints,
        BrokerName brokerName,
        string fullyQualifiedNamespace, TokenCredential tokenCredential,
        Action<ServiceBusClientOptions>? configure = null)
    {
        AzureServiceBusTransport transport = endpoints.AzureServiceBusTransport(brokerName);
        transport.FullyQualifiedNamespace = fullyQualifiedNamespace ?? throw new ArgumentNullException(nameof(fullyQualifiedNamespace));
        transport.TokenCredential = tokenCredential ?? throw new ArgumentNullException(nameof(tokenCredential));
        configure?.Invoke(transport.ClientOptions);

        return new AzureServiceBusConfiguration(transport, endpoints);
    }

    /// <summary>
    /// Connect to Azure Service Bus using a namespace and secured through an AzureNamedKeyCredential
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="brokerName"></param>
    /// <param name="fullyQualifiedNamespace"></param>
    /// <param name="namedKeyCredential"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static AzureServiceBusConfiguration AddNamedAzureServiceBusBroker(WolverineOptions endpoints,
        BrokerName brokerName,
        string fullyQualifiedNamespace, AzureNamedKeyCredential namedKeyCredential,
        Action<ServiceBusClientOptions>? configure = null)
    {
        AzureServiceBusTransport transport = endpoints.AzureServiceBusTransport(brokerName);
        transport.FullyQualifiedNamespace = fullyQualifiedNamespace ?? throw new ArgumentNullException(nameof(fullyQualifiedNamespace));
        transport.NamedKeyCredential = namedKeyCredential ?? throw new ArgumentNullException(nameof(namedKeyCredential));
        configure?.Invoke(transport.ClientOptions);

        return new AzureServiceBusConfiguration(transport, endpoints);
    }

    /// <summary>
    /// Connect to Azure Service Bus using a namespace and secured through an AzureSasCredential
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="brokerName"></param>
    /// <param name="fullyQualifiedNamespace"></param>
    /// <param name="sasCredential"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static AzureServiceBusConfiguration AddNamedAzureServiceBusBroker(this WolverineOptions endpoints,
        BrokerName brokerName,
        string fullyQualifiedNamespace, AzureSasCredential sasCredential,
        Action<ServiceBusClientOptions>? configure = null)
    {
        AzureServiceBusTransport transport = endpoints.AzureServiceBusTransport(brokerName);
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
        AzureServiceBusTransport transport = endpoints.AzureServiceBusTransport();

        string corrected = transport.MaybeCorrectName(queueName);
        AzureServiceBusQueue endpoint = transport.Queues[corrected];
        endpoint.EndpointName = queueName;
        endpoint.IsListener = true;

        configure?.Invoke(endpoint);

        return new AzureServiceBusQueueListenerConfiguration(endpoint);
    }

    /// <summary>
    ///     Listen for incoming messages at the azure service bus queue by name on a named broker
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="brokerName">Name of the broker</param>
    /// <param name="queueName">The name of the Azuer service bus queue</param>
    /// <param name="configure">Optional configuration</param>
    /// <returns></returns>
    public static AzureServiceBusQueueListenerConfiguration ListenToAzureServiceBusQueueOnNamedBroker(
        this WolverineOptions endpoints, BrokerName brokerName, string queueName,
        Action<AzureServiceBusQueue>? configure = null)
    {
        AzureServiceBusTransport transport = endpoints.AzureServiceBusTransport(brokerName);

        string corrected = transport.MaybeCorrectName(queueName);
        AzureServiceBusQueue endpoint = transport.Queues[corrected];
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

            AzureServiceBusTopic topic = _transport.Topics[topicName];
            configureTopic?.Invoke(topic.Options);

            AzureServiceBusSubscription subscription = topic.FindOrCreateSubscription(_subscriptionName);
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

        AzureServiceBusTransport transport = endpoints.AzureServiceBusTransport();

        return new SubscriptionExpression(
            transport.MaybeCorrectName(subscriptionName),
            configureSubscriptions,
            configureSubscriptionRule,
            transport);
    }

    /// <summary>
    /// Listen for messages from an Azure Service Bus topic subscription on a named broker
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="brokerName"></param>
    /// <param name="subscriptionName"></param>
    /// <param name="configureSubscriptions">Optionally apply customizations to the actual Azure Service Bus subscription</param>
    /// <param name="configureSubscriptionRule">Optionally apply customizations to the Azure Service Bus subscription rule</param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static SubscriptionExpression ListenToAzureServiceBusSubscriptionOnNamedBroker(
        this WolverineOptions endpoints,
        BrokerName brokerName,
        string subscriptionName,
        Action<CreateSubscriptionOptions>? configureSubscriptions = null,
        Action<CreateRuleOptions>? configureSubscriptionRule = null)
    {
        if (subscriptionName == null)
        {
            throw new ArgumentNullException(nameof(subscriptionName));
        }

        AzureServiceBusTransport transport = endpoints.AzureServiceBusTransport(brokerName);

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
        TransportCollection transports = publishing.As<PublishingExpression>().Parent.Transports;
        AzureServiceBusTransport transport = transports.GetOrCreate<AzureServiceBusTransport>();

        string corrected = transport.MaybeCorrectName(queueName);

        AzureServiceBusQueue endpoint = transport.Queues[corrected];
        endpoint.EndpointName = queueName;

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(endpoint.Uri);

        return new AzureServiceBusQueueSubscriberConfiguration(endpoint);
    }

    /// <summary>
    /// Publish the designated messages directly to an Azure Service Bus queue on a named broker
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="brokerName"></param>
    /// <param name="queueName"></param>
    /// <returns></returns>
    public static AzureServiceBusQueueSubscriberConfiguration ToAzureServiceBusQueueOnNamedBroker(
        this IPublishToExpression publishing, BrokerName brokerName, string queueName)
    {
        TransportCollection transports = publishing.As<PublishingExpression>().Parent.Transports;
        AzureServiceBusTransport transport = transports.GetOrCreate<AzureServiceBusTransport>(brokerName);

        string corrected = transport.MaybeCorrectName(queueName);

        AzureServiceBusQueue endpoint = transport.Queues[corrected];
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
        TransportCollection transports = publishing.As<PublishingExpression>().Parent.Transports;
        AzureServiceBusTransport transport = transports.GetOrCreate<AzureServiceBusTransport>();

        string corrected = transport.MaybeCorrectName(topicName);

        AzureServiceBusTopic endpoint = transport.Topics[corrected];
        endpoint.EndpointName = topicName;

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(endpoint.Uri);

        return new AzureServiceBusTopicSubscriberConfiguration(endpoint);
    }

    /// <summary>
    /// Publish the designated messages to an Azure Service Bus topic on a named broker
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="brokerName"></param>
    /// <param name="topicName"></param>
    /// <returns></returns>
    public static AzureServiceBusTopicSubscriberConfiguration ToAzureServiceBusTopicOnNamedBroker(
        this IPublishToExpression publishing, BrokerName brokerName, string topicName)
    {
        TransportCollection transports = publishing.As<PublishingExpression>().Parent.Transports;
        AzureServiceBusTransport transport = transports.GetOrCreate<AzureServiceBusTransport>(brokerName);

        string corrected = transport.MaybeCorrectName(topicName);

        AzureServiceBusTopic endpoint = transport.Topics[corrected];
        endpoint.EndpointName = topicName;

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(endpoint.Uri);

        return new AzureServiceBusTopicSubscriberConfiguration(endpoint);
    }

    /// <summary>
    /// Create a sharded message topology with Azure Service Bus queues named
    /// baseName1, baseName2, etc.
    /// </summary>
    /// <param name="rules"></param>
    /// <param name="baseName"></param>
    /// <param name="numberOfEndpoints"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static MessagePartitioningRules PublishToShardedAzureServiceBusQueues(this MessagePartitioningRules rules, string baseName, int numberOfEndpoints, Action<PartitionedMessageTopologyWithQueues> configure)
    {
        rules.AddPublishingTopology((opts, _) =>
        {
            var topology = new PartitionedMessageTopologyWithQueues(opts, PartitionSlots.Five, baseName, numberOfEndpoints);
            topology.ConfigureListening(x => {});
            configure(topology);
            topology.AssertValidity();

            return topology;
        });

        return rules;
    }

    /// <summary>
    /// Use sharded Azure Service Bus queues for global partitioned message processing.
    /// Queues will be named baseName1, baseName2, etc.
    /// </summary>
    /// <param name="topology"></param>
    /// <param name="baseName"></param>
    /// <param name="numberOfEndpoints"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static GlobalPartitionedMessageTopology UseShardedAzureServiceBusQueues(
        this GlobalPartitionedMessageTopology topology, string baseName, int numberOfEndpoints,
        Action<PartitionedMessageTopologyWithQueues>? configure = null)
    {
        topology.SetExternalTopology(opts =>
        {
            var t = new PartitionedMessageTopologyWithQueues(opts, PartitionSlots.Five, baseName, numberOfEndpoints);
            configure?.Invoke(t);
            return t;
        }, baseName);
        return topology;
    }
}