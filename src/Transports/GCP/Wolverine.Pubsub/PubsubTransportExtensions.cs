using Grpc.Core;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Pubsub.Internal;
using Wolverine.Runtime.Partitioning;

namespace Wolverine.Pubsub;

public static class PubsubTransportExtensions
{
    /// <summary>
    ///     Quick access to the Google Cloud Platform Pub/Sub Transport within this application.
    ///     This is for advanced usage.
    /// </summary>
    /// <param name="endpoints"></param>
    /// <returns></returns>
    internal static PubsubTransport PubsubTransport(this WolverineOptions endpoints, BrokerName? name = null)
    {
        var transports = endpoints.As<WolverineOptions>().Transports;

        return transports.GetOrCreate<PubsubTransport>(name);
    }

    /// <summary>
    ///     Additive configuration to the Google Cloud Platform Pub/Sub integration for this Wolverine application.
    /// </summary>
    /// <param name="endpoints"></param>
    /// <returns></returns>
    public static PubsubConfiguration ConfigurePubsub(this WolverineOptions endpoints)
    {
        return new PubsubConfiguration(endpoints.PubsubTransport(), endpoints);
    }

    /// <summary>
    ///     Connect to Google Cloud Platform Pub/Sub with a project id.
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="projectId"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static PubsubConfiguration UsePubsub(this WolverineOptions endpoints, string projectId,
        Action<PubsubTransport>? configure = null)
    {
        var transport = endpoints.PubsubTransport();

        transport.ProjectId = projectId ?? throw new ArgumentNullException(nameof(projectId));

        configure?.Invoke(transport);

        return new PubsubConfiguration(transport, endpoints);
    }

    /// <summary>
    ///     Add an additional, named broker connection to Google Cloud Platform Pub/Sub alongside the default one.
    ///     Endpoints created through the <c>...OnNamedBroker</c> overloads are pinned to this broker. Note that the
    ///     Wolverine <see cref="Uri" /> scheme for endpoints on a named broker is the broker name itself
    ///     (e.g. <c>{name}://{projectId}/{topic}</c>), not <c>pubsub</c>.
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="name">The unique name of the additional broker.</param>
    /// <param name="projectId">The GCP project id for this named broker.</param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static PubsubConfiguration AddNamedPubsubBroker(this WolverineOptions endpoints, BrokerName name,
        string projectId, Action<PubsubTransport>? configure = null)
    {
        var transport = endpoints.PubsubTransport(name);

        transport.ProjectId = projectId ?? throw new ArgumentNullException(nameof(projectId));

        configure?.Invoke(transport);

        return new PubsubConfiguration(transport, endpoints);
    }

    /// <summary>
    /// Connect to a Google Cloud Platform Pub/Sub <b>emulator</b> whose endpoint comes from the named connection
    /// string in IConfiguration. This option is useful for Aspire integration between Wolverine and Aspire, e.g.
    /// the CommunityToolkit Floci GCP emulator (<c>builder.AddFlociGcp("gcp")</c>), whose connection string is the
    /// emulator URL <c>http://host:port</c>.
    ///
    /// <para>
    /// The value is the emulator endpoint, either as a URL (<c>http://host:port</c>, or <c>https://host:port</c> for
    /// TLS) or as a bare <c>host:port</c> (the <c>PUBSUB_EMULATOR_HOST</c> format). The publisher, subscriber API and
    /// streaming subscriber clients are pointed at that endpoint with insecure channel credentials (TLS credentials
    /// for <c>https</c>). Do not combine this with <c>UseEmulatorDetection()</c>.
    /// </para>
    ///
    /// <para>
    /// The project id is still passed explicitly because Wolverine bakes it into every Pub/Sub endpoint's
    /// <see cref="Uri" /> (<c>pubsub://{projectId}/{topic}</c>) and topic name as the endpoints are configured, which
    /// is before IConfiguration can be read. Production Pub/Sub has no connection string; use
    /// <see cref="UsePubsub" /> there.
    /// </para>
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="projectId">The GCP project id</param>
    /// <param name="connectionStringName">Key of the expected connection string from IConfiguration</param>
    /// <param name="configure">Optional extra configuration of the transport</param>
    /// <returns></returns>
    public static PubsubConfiguration UsePubsubUsingNamedConnection(this WolverineOptions endpoints, string projectId,
        string connectionStringName, Action<PubsubTransport>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringName);

        var transport = endpoints.PubsubTransport();
        transport.ProjectId = projectId ?? throw new ArgumentNullException(nameof(projectId));
        configure?.Invoke(transport);

        applyNamedConnection(endpoints, transport, connectionStringName);

        return new PubsubConfiguration(transport, endpoints);
    }

    /// <summary>
    /// Add an additional, named broker connection to a Google Cloud Platform Pub/Sub <b>emulator</b> whose endpoint
    /// comes from the named connection string in IConfiguration. This is the Aspire-friendly form of
    /// <see cref="AddNamedPubsubBroker" />; the value formats are the same as <see cref="UsePubsubUsingNamedConnection" />.
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="name">The unique name of the additional broker.</param>
    /// <param name="projectId">The GCP project id for this named broker.</param>
    /// <param name="connectionStringName">Key of the expected connection string from IConfiguration</param>
    /// <param name="configure">Optional extra configuration of the transport</param>
    /// <returns></returns>
    public static PubsubConfiguration AddNamedPubsubBrokerUsingNamedConnection(this WolverineOptions endpoints,
        BrokerName name, string projectId, string connectionStringName, Action<PubsubTransport>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringName);

        var transport = endpoints.PubsubTransport(name);
        transport.ProjectId = projectId ?? throw new ArgumentNullException(nameof(projectId));
        configure?.Invoke(transport);

        applyNamedConnection(endpoints, transport, connectionStringName);

        return new PubsubConfiguration(transport, endpoints);
    }

    private static void applyNamedConnection(WolverineOptions endpoints, PubsubTransport transport,
        string connectionStringName)
    {
        // GH-5126: the client builder hooks are only invoked when the transport connects (ConnectAsync) and when
        // each listener builds its streaming subscriber, which is after the named connection string has been
        // resolved and applied at the top of host startup. The emulator endpoint is applied FIRST, ahead of any
        // ConfigurePublisherApiClient()/ConfigureSubscriberApiClient()/ConfigureSubscriberClient() callbacks, so
        // those can still override it.
        endpoints.ApplyNamedConnectionString("GCP Pub/Sub", connectionStringName, "AddFlociGcp", value =>
        {
            var (endpoint, credentials) = ParseEmulatorConnectionString(value);
            transport.EmulatorEndpoint = endpoint;

            var publisher = transport.ConfigurePublisherApiBuilder;
            transport.ConfigurePublisherApiBuilder = async b =>
            {
                b.Endpoint = endpoint;
                b.ChannelCredentials = credentials;
                if (publisher != null) await publisher(b);
            };

            var subscriberApi = transport.ConfigureSubscriberApiBuilder;
            transport.ConfigureSubscriberApiBuilder = async b =>
            {
                b.Endpoint = endpoint;
                b.ChannelCredentials = credentials;
                if (subscriberApi != null) await subscriberApi(b);
            };

            var subscriberClient = transport.ConfigureSubscriberClientBuilder;
            transport.ConfigureSubscriberClientBuilder = async b =>
            {
                b.Endpoint = endpoint;
                b.ChannelCredentials = credentials;
                if (subscriberClient != null) await subscriberClient(b);
            };
        });
    }

    /// <summary>
    /// Parse a Pub/Sub emulator connection string -- <c>http://host:port</c>, <c>https://host:port</c> or a bare
    /// <c>host:port</c> -- into the gRPC endpoint and the channel credentials to use against it
    /// </summary>
    internal static (string Endpoint, ChannelCredentials Credentials) ParseEmulatorConnectionString(string value)
    {
        var trimmed = value.Trim();
        var useTls = false;

        if (trimmed.Contains("://"))
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Host.IsEmpty())
            {
                throw new ArgumentOutOfRangeException(nameof(value),
                    $"'{value}' is not a valid GCP Pub/Sub emulator endpoint. Expected something like 'http://localhost:8085'");
            }

            useTls = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
            var host = uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.DnsSafeHost}]" : uri.Host;
            trimmed = $"{host}:{uri.Port}";
        }
        else if (trimmed.IsEmpty())
        {
            throw new ArgumentOutOfRangeException(nameof(value), "The GCP Pub/Sub emulator endpoint is empty");
        }

        return (trimmed, useTls ? ChannelCredentials.SecureSsl : ChannelCredentials.Insecure);
    }

    /// <summary>
    ///     Listen for incoming messages at the designated Google Cloud Platform Pub/Sub topic on a named broker.
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="name">The name of the additional broker registered via <see cref="AddNamedPubsubBroker" />.</param>
    /// <param name="topicName">The name of the Google Cloud Platform Pub/Sub topic</param>
    /// <param name="configure">
    ///     Optional configuration for this Google Cloud Platform Pub/Sub endpoint.
    /// </param>
    /// <returns></returns>
    public static PubsubTopicListenerConfiguration ListenToPubsubTopicOnNamedBroker(
        this WolverineOptions endpoints,
        BrokerName name,
        string topicName,
        Action<PubsubEndpoint>? configure = null
    )
    {
        var transport = endpoints.PubsubTransport(name);
        var topic = transport.Topics[transport.MaybeCorrectName(topicName)];

        topic.EndpointName = topicName;
        topic.IsListener = true;

        configure?.Invoke(topic);

        return new PubsubTopicListenerConfiguration(topic);
    }

    /// <summary>
    ///     Publish the designated messages to a Google Cloud Platform Pub/Sub topic on a named broker.
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="name">The name of the additional broker registered via <see cref="AddNamedPubsubBroker" />.</param>
    /// <param name="topicName"></param>
    /// <param name="configure">
    ///     Optional configuration for this Google Cloud Platform Pub/Sub endpoint.
    /// </param>
    /// <returns></returns>
    public static PubsubTopicSubscriberConfiguration ToPubsubTopicOnNamedBroker(
        this IPublishToExpression publishing,
        BrokerName name,
        string topicName,
        Action<PubsubEndpoint>? configure = null
    )
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<PubsubTransport>(name);
        var topic = transport.Topics[transport.MaybeCorrectName(topicName)];

        topic.EndpointName = topicName;

        configure?.Invoke(topic);

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(topic.Uri);

        return new PubsubTopicSubscriberConfiguration(topic);
    }

    /// <summary>
    ///     Listen for incoming messages at the designated Google Cloud Platform Pub/Sub topic by name.
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="topicName">The name of the Google Cloud Platform Pub/Sub topic</param>
    /// <param name="configure">
    ///     Optional configuration for this Google Cloud Platform Pub/Sub endpoint.
    /// </param>
    /// <returns></returns>
    public static PubsubTopicListenerConfiguration ListenToPubsubTopic(
        this WolverineOptions endpoints,
        string topicName,
        Action<PubsubEndpoint>? configure = null
    )
    {
        var transport = endpoints.PubsubTransport();
        var topic = transport.Topics[transport.MaybeCorrectName(topicName)];

        topic.EndpointName = topicName;
        topic.IsListener = true;

        configure?.Invoke(topic);

        return new PubsubTopicListenerConfiguration(topic);
    }

    /// <summary>
    ///     Listen for incoming messages on an existing Google Cloud Platform Pub/Sub subscription.
    ///     Since an existing subscription is used, IdentifierPrefix is not applied to the subscription name.
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="subscriptionName">The name of the Google Cloud Platform Pub/Sub subscription</param>
    /// <param name="configure">
    ///     Optional configuration for this Google Cloud Platform Pub/Sub endpoint.
    /// </param>
    /// <returns></returns>
    public static PubsubTopicListenerConfiguration ListenToPubsubSubscription(
        this WolverineOptions endpoints,
        string subscriptionName,
        Action<PubsubEndpoint>? configure = null
    )
    {
        var transport = endpoints.PubsubTransport();
        var topic = transport.Topics[subscriptionName];

        topic.IsListener = true;
        topic.IsExistingSubscription = true;

        configure?.Invoke(topic);

        return new PubsubTopicListenerConfiguration(topic);
    }

    /// <summary>
    ///     Listen for incoming messages on an existing Google Cloud Platform Pub/Sub subscription on a named broker.
    ///     Since an existing subscription is used, IdentifierPrefix is not applied to the subscription name.
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="name">The name of the additional broker registered via <see cref="AddNamedPubsubBroker" />.</param>
    /// <param name="subscriptionName">The name of the Google Cloud Platform Pub/Sub subscription</param>
    /// <param name="configure">
    ///     Optional configuration for this Google Cloud Platform Pub/Sub endpoint.
    /// </param>
    /// <returns></returns>
    public static PubsubTopicListenerConfiguration ListenToPubsubSubscriptionOnNamedBroker(
        this WolverineOptions endpoints,
        BrokerName name,
        string subscriptionName,
        Action<PubsubEndpoint>? configure = null
    )
    {
        var transport = endpoints.PubsubTransport(name);
        var topic = transport.Topics[subscriptionName];

        topic.IsListener = true;
        topic.IsExistingSubscription = true;

        configure?.Invoke(topic);

        return new PubsubTopicListenerConfiguration(topic);
    }

    /// <summary>
    ///     Publish the designated messages to a Google Cloud Platform Pub/Sub topic.
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="topicName"></param>
    /// <param name="configure">
    ///     Optional configuration for this Google Cloud Platform Pub/Sub endpoint.
    /// </param>
    /// <returns></returns>
    public static PubsubTopicSubscriberConfiguration ToPubsubTopic(
        this IPublishToExpression publishing,
        string topicName,
        Action<PubsubEndpoint>? configure = null
    )
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<PubsubTransport>();
        var topic = transport.Topics[transport.MaybeCorrectName(topicName)];

        topic.EndpointName = topicName;

        configure?.Invoke(topic);

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(topic.Uri);

        return new PubsubTopicSubscriberConfiguration(topic);
    }

    /// <summary>
    /// Create a sharded message topology with Google Cloud Pub/Sub topics named
    /// baseName1, baseName2, etc.
    /// </summary>
    /// <param name="rules"></param>
    /// <param name="baseName"></param>
    /// <param name="numberOfEndpoints"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static MessagePartitioningRules PublishToShardedPubsubTopics(this MessagePartitioningRules rules, string baseName, int numberOfEndpoints, Action<PartitionedMessageTopologyWithTopics> configure)
    {
        rules.AddPublishingTopology((opts, _) =>
        {
            var topology = new PartitionedMessageTopologyWithTopics(opts, PartitionSlots.Five, baseName, numberOfEndpoints);
            topology.ConfigureListening(x => {});
            configure(topology);
            topology.AssertValidity();

            return topology;
        });

        return rules;
    }

    /// <summary>
    /// Use sharded Google Cloud Pub/Sub topics for global partitioned message processing.
    /// Topics will be named baseName1, baseName2, etc.
    /// </summary>
    /// <param name="topology"></param>
    /// <param name="baseName"></param>
    /// <param name="numberOfEndpoints"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static GlobalPartitionedMessageTopology UseShardedPubsubTopics(
        this GlobalPartitionedMessageTopology topology, string baseName, int numberOfEndpoints,
        Action<PartitionedMessageTopologyWithTopics>? configure = null)
    {
        topology.SetExternalTopology(opts =>
        {
            var t = new PartitionedMessageTopologyWithTopics(opts, PartitionSlots.Five, baseName, numberOfEndpoints);
            configure?.Invoke(t);
            return t;
        }, baseName);
        return topology;
    }

    /// <summary>
    /// Create a sharded message topology with Google Cloud Pub/Sub topics named
    /// baseName1, baseName2, etc. on a named broker registered via <see cref="AddNamedPubsubBroker" />.
    /// </summary>
    /// <param name="rules"></param>
    /// <param name="name">The name of the additional broker registered via <see cref="AddNamedPubsubBroker" />.</param>
    /// <param name="baseName"></param>
    /// <param name="numberOfEndpoints"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static MessagePartitioningRules PublishToShardedPubsubTopicsOnNamedBroker(this MessagePartitioningRules rules,
        BrokerName name, string baseName, int numberOfEndpoints, Action<PartitionedMessageTopologyWithTopics> configure)
    {
        rules.AddPublishingTopology((opts, _) =>
        {
            var topology = new PartitionedMessageTopologyWithTopics(opts, PartitionSlots.Five, baseName, numberOfEndpoints, name);
            topology.ConfigureListening(x => {});
            configure(topology);
            topology.AssertValidity();

            return topology;
        });

        return rules;
    }

    /// <summary>
    /// Use sharded Google Cloud Pub/Sub topics on a named broker registered via <see cref="AddNamedPubsubBroker" />
    /// for global partitioned message processing. Topics will be named baseName1, baseName2, etc.
    /// </summary>
    /// <param name="topology"></param>
    /// <param name="name">The name of the additional broker registered via <see cref="AddNamedPubsubBroker" />.</param>
    /// <param name="baseName"></param>
    /// <param name="numberOfEndpoints"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static GlobalPartitionedMessageTopology UseShardedPubsubTopicsOnNamedBroker(
        this GlobalPartitionedMessageTopology topology, BrokerName name, string baseName, int numberOfEndpoints,
        Action<PartitionedMessageTopologyWithTopics>? configure = null)
    {
        topology.SetExternalTopology(opts =>
        {
            var t = new PartitionedMessageTopologyWithTopics(opts, PartitionSlots.Five, baseName, numberOfEndpoints, name);
            configure?.Invoke(t);
            return t;
        }, baseName);
        return topology;
    }
}