using Amazon.SQS;
using JasperFx.Core.Reflection;
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
}