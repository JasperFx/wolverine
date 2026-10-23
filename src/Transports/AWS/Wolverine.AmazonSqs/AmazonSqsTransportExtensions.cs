using Amazon.SQS;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;
using Wolverine.Runtime.Partitioning;

namespace Wolverine.AmazonSqs;

public static class AmazonSqsTransportExtensions
{
    /// <summary>
    ///     Quick access to the Amazon SQS Transport within this application.
    ///     This is for advanced usage
    /// </summary>
    /// <param name="endpoints"></param>
    /// <returns></returns>
    internal static AmazonSqsTransport AmazonSqsTransport(this WolverineOptions endpoints, BrokerName? name = null)
    {
        var transports = endpoints.As<WolverineOptions>().Transports;

        return transports.GetOrCreate<AmazonSqsTransport>(name);
    }

    /// <summary>
    /// Apply additive configuration to the AWS SQS integration within this Wolverine application
    /// </summary>
    /// <param name="options"></param>
    /// <returns></returns>
    public static AmazonSqsTransportConfiguration ConfigureAmazonSqsTransport(this WolverineOptions options)
    {
        var transport = options.AmazonSqsTransport();
        return new AmazonSqsTransportConfiguration(transport, options);
    }

    public static AmazonSqsTransportConfiguration UseAmazonSqsTransport(this WolverineOptions options)
    {
        var transport = options.AmazonSqsTransport();
        return new AmazonSqsTransportConfiguration(transport, options);
    }

    public static AmazonSqsTransportConfiguration UseAmazonSqsTransport(this WolverineOptions options,
        Action<AmazonSQSConfig> configuration)
    {
        var transport = options.AmazonSqsTransport();
        configuration(transport.Config);
        return new AmazonSqsTransportConfiguration(transport, options);
    }
    
    /// <summary>
    /// Direct Wolverine to connect to Amazon SQS through the service URL held in the named connection string
    /// from IConfiguration. This option is useful for Aspire integration between Wolverine and a local AWS
    /// emulator: the LocalStack (<c>AddLocalStack</c>) and CommunityToolkit Floci (<c>AddFlociAws</c>) hosting
    /// integrations put the emulator's service URL (e.g. <c>http://localhost:4566</c>) at
    /// <c>ConnectionStrings:{name}</c>, and Wolverine applies it as <see cref="AmazonSQSConfig.ServiceURL"/>.
    /// Unless you configure credentials explicitly, the same placeholder credentials as
    /// <c>UseAmazonSqsTransportLocally()</c> are used.
    /// <para>
    /// Real AWS has no connection string under Aspire (<c>Aspire.Hosting.AWS</c> passes the profile and region
    /// as the <c>AWS_PROFILE</c>/<c>AWS_REGION</c> environment variables that the AWS SDK already reads), so
    /// use <c>UseAmazonSqsTransport()</c> or <see cref="UseAmazonSqsTransportWithClientFromContainer"/> for that.
    /// </para>
    /// </summary>
    /// <param name="options"></param>
    /// <param name="connectionStringName">Key of the expected connection string from IConfiguration</param>
    /// <param name="configuration">Optional extra configuration of the AmazonSQSConfig</param>
    /// <returns></returns>
    public static AmazonSqsTransportConfiguration UseAmazonSqsTransportUsingNamedConnection(this WolverineOptions options,
        string connectionStringName, Action<AmazonSQSConfig>? configuration = null)
    {
        var transport = options.AmazonSqsTransport();
        return options.applyNamedConnection(transport, connectionStringName, configuration);
    }

    /// <summary>
    /// Add an additional, named broker connection to Amazon SQS through the service URL held in the named
    /// connection string from IConfiguration. See <see cref="UseAmazonSqsTransportUsingNamedConnection"/>
    /// </summary>
    /// <param name="options"></param>
    /// <param name="name">The name of the additional broker</param>
    /// <param name="connectionStringName">Key of the expected connection string from IConfiguration</param>
    /// <param name="configuration">Optional extra configuration of the AmazonSQSConfig</param>
    /// <returns></returns>
    public static AmazonSqsTransportConfiguration AddNamedAmazonSqsBrokerUsingNamedConnection(this WolverineOptions options,
        BrokerName name, string connectionStringName, Action<AmazonSQSConfig>? configuration = null)
    {
        var transport = options.AmazonSqsTransport(name);
        return options.applyNamedConnection(transport, connectionStringName, configuration);
    }

    private static AmazonSqsTransportConfiguration applyNamedConnection(this WolverineOptions options,
        AmazonSqsTransport transport, string connectionStringName, Action<AmazonSQSConfig>? configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringName);

        configuration?.Invoke(transport.Config);

        // The SQS client is only built from Config in ConnectAsync, which runs after this callback
        options.ApplyNamedConnectionString("Amazon SQS", connectionStringName, "AddLocalStack",
            value => transport.ApplyNamedServiceUrl(connectionStringName, value));

        return new AmazonSqsTransportConfiguration(transport, options);
    }

    /// <summary>
    /// Use the <see cref="IAmazonSQS"/> client registered in the application's IoC container -- for example by
    /// <c>services.AddAWSService&lt;IAmazonSQS&gt;()</c> from AWSSDK.Extensions.NETCore.Setup -- rather than
    /// having Wolverine build one from its own <see cref="AmazonSQSConfig"/>. The client is resolved when the
    /// transport connects, and Wolverine does not dispose it.
    /// </summary>
    /// <param name="options"></param>
    /// <param name="clientSource">
    /// Optional way to pick the client out of the container, e.g. a keyed service. By default this resolves
    /// <see cref="IAmazonSQS"/>
    /// </param>
    /// <returns></returns>
    public static AmazonSqsTransportConfiguration UseAmazonSqsTransportWithClientFromContainer(this WolverineOptions options,
        Func<IServiceProvider, IAmazonSQS>? clientSource = null)
    {
        var transport = options.AmazonSqsTransport();
        return options.useClientFromContainer(transport, clientSource);
    }

    /// <summary>
    /// Add an additional, named broker connection to Amazon SQS that uses a <see cref="IAmazonSQS"/> client from
    /// the application's IoC container. See <see cref="UseAmazonSqsTransportWithClientFromContainer"/>
    /// </summary>
    /// <param name="options"></param>
    /// <param name="name">The name of the additional broker</param>
    /// <param name="clientSource">Picks this broker's client out of the container, e.g. a keyed service</param>
    /// <returns></returns>
    public static AmazonSqsTransportConfiguration AddNamedAmazonSqsBrokerWithClientFromContainer(this WolverineOptions options,
        BrokerName name, Func<IServiceProvider, IAmazonSQS> clientSource)
    {
        ArgumentNullException.ThrowIfNull(clientSource);
        var transport = options.AmazonSqsTransport(name);
        return options.useClientFromContainer(transport, clientSource);
    }

    private static AmazonSqsTransportConfiguration useClientFromContainer(this WolverineOptions options,
        AmazonSqsTransport transport, Func<IServiceProvider, IAmazonSQS>? clientSource)
    {
        clientSource ??= services => services.GetRequiredService<IAmazonSQS>();
        transport.ClientSource = runtime => clientSource(runtime.Services);
        return new AmazonSqsTransportConfiguration(transport, options);
    }

    /// <summary>
    /// Add an additional, named broker connection to Amazon SQS
    /// </summary>
    /// <param name="options"></param>
    /// <param name="name"></param>
    /// <returns></returns>
    public static AmazonSqsTransportConfiguration AddNamedAmazonSqsBroker(this WolverineOptions options, BrokerName name)
    {
        var transport = options.AmazonSqsTransport(name);
        return new AmazonSqsTransportConfiguration(transport, options);
    }

    /// <summary>
    /// Add an additional, named broker connection to Amazon SQS
    /// </summary>
    /// <param name="options"></param>
    /// <param name="name"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    public static AmazonSqsTransportConfiguration AddNamedAmazonSqsBroker(this WolverineOptions options, BrokerName name,
        Action<AmazonSQSConfig> configuration)
    {
        var transport = options.AmazonSqsTransport(name);
        configuration(transport.Config);
        return new AmazonSqsTransportConfiguration(transport, options);
    }

    /// <summary>
    ///     Sets up a connection to a locally running Amazon SQS LocalStack
    ///     broker for development or testing purposes
    /// </summary>
    /// <param name="port">Port for SQS. Default is 4566</param>
    /// <returns></returns>
    public static AmazonSqsTransportConfiguration UseAmazonSqsTransportLocally(this WolverineOptions options,
        int port = 4566)
    {
        var transport = options.AmazonSqsTransport();

        transport.ConnectToLocalStack(port);

        return new AmazonSqsTransportConfiguration(transport, options);
    }
    
    /// <summary>
    ///     Sets up a connection to a locally running Amazon SQS LocalStack
    ///     broker for development or testing purposes
    /// </summary>
    /// <param name="port">Port for SQS. Default is 4566</param>
    /// <returns></returns>
    public static AmazonSqsTransportConfiguration UseAmazonSqsTransportLocallyAsNamedBroker(this WolverineOptions options, BrokerName name,
        int port = 4566)
    {
        var transport = options.AmazonSqsTransport(name);

        transport.ConnectToLocalStack(port);

        return new AmazonSqsTransportConfiguration(transport, options);
    }

    /// <summary>
    ///     Listen for incoming messages at the designated Amazon SQS queue by name
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="queueName">The name of the SQS queue</param>
    /// <param name="configure">
    ///     Optional configuration for this SQS queue if being initialized by Wolverine
    ///     <returns></returns>
    public static AmazonSqsListenerConfiguration ListenToSqsQueue(this WolverineOptions endpoints, string queueName,
        Action<AmazonSqsQueue>? configure = null)
    {
        var transport = endpoints.AmazonSqsTransport();

        var corrected = transport.MaybeCorrectName(queueName);
        var endpoint = transport.EndpointForQueue(corrected);
        endpoint.EndpointName = queueName;
        endpoint.IsListener = true;

        configure?.Invoke(endpoint);

        return new AmazonSqsListenerConfiguration(endpoint);
    }
    
    /// <summary>
    ///     Listen for incoming messages at the designated Amazon SQS queue by name
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="queueName">The name of the SQS queue</param>
    /// <param name="configure">
    ///     Optional configuration for this SQS queue if being initialized by Wolverine
    ///     <returns></returns>
    public static AmazonSqsListenerConfiguration ListenToSqsQueueOnNamedBroker(this WolverineOptions endpoints, BrokerName name, string queueName,
        Action<AmazonSqsQueue>? configure = null)
    {
        var transport = endpoints.AmazonSqsTransport(name);

        var corrected = transport.MaybeCorrectName(queueName);
        var endpoint = transport.EndpointForQueue(corrected);
        endpoint.EndpointName = queueName;
        endpoint.IsListener = true;

        configure?.Invoke(endpoint);

        return new AmazonSqsListenerConfiguration(endpoint);
    }

    /// <summary>
    ///     Publish matching messages directly to an Amazon SQS queue
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="queueName">The name of the SQS queue</param>
    /// <returns></returns>
    public static AmazonSqsSubscriberConfiguration ToSqsQueue(this IPublishToExpression publishing, string queueName)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<AmazonSqsTransport>();

        var corrected = transport.MaybeCorrectName(queueName);

        var endpoint = transport.EndpointForQueue(corrected);
        endpoint.EndpointName = queueName;

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(endpoint.Uri);

        return new AmazonSqsSubscriberConfiguration(endpoint);
    }
    
    /// <summary>
    ///     Publish matching messages directly to an Amazon SQS queue
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="queueName">The name of the SQS queue</param>
    /// <returns></returns>
    public static AmazonSqsSubscriberConfiguration ToSqsQueueOnNamedBroker(this IPublishToExpression publishing, BrokerName name, string queueName)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<AmazonSqsTransport>(name);

        var corrected = transport.MaybeCorrectName(queueName);

        var endpoint = transport.EndpointForQueue(corrected);
        endpoint.EndpointName = queueName;

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(endpoint.Uri);

        return new AmazonSqsSubscriberConfiguration(endpoint);
    }
    
    /// <summary>
    /// Create a sharded message topology with Amazon SQS queues named
    /// baseName1, baseName2, etc.
    /// </summary>
    /// <param name="rules"></param>
    /// <param name="baseName"></param>
    /// <param name="numberOfEndpoints"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static MessagePartitioningRules PublishToShardedAmazonSqsQueues(this MessagePartitioningRules rules, string baseName, int numberOfEndpoints, Action<PartitionedMessageTopologyWithQueues> configure)
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
    /// Use sharded Amazon SQS queues for global partitioned message processing.
    /// Queues will be named baseName1, baseName2, etc.
    /// </summary>
    /// <param name="topology"></param>
    /// <param name="baseName"></param>
    /// <param name="numberOfEndpoints"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static GlobalPartitionedMessageTopology UseShardedAmazonSqsQueues(
        this GlobalPartitionedMessageTopology topology, string baseName, int numberOfEndpoints,
        Action<PartitionedMessageTopologyWithQueues>? configure = null)
    {
        topology.SetExternalTopology(opts =>
        {
            var t = new PartitionedMessageTopologyWithQueues(opts, PartitionSlots.Five, baseName, numberOfEndpoints);
            t.ConfigureListening(x => {});
            configure?.Invoke(t);
            return t;
        }, baseName);
        return topology;
    }
}