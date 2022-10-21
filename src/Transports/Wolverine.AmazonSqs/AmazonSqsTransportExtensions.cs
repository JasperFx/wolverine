using Amazon.Runtime;
using Amazon.SQS;
using Baseline;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;

namespace Wolverine.AmazonSqs;

public static class AmazonSqsTransportExtensions
{
    /// <summary>
    ///     Quick access to the Rabbit MQ Transport within this application.
    ///     This is for advanced usage
    /// </summary>
    /// <param name="endpoints"></param>
    /// <returns></returns>
    internal static AmazonSqsTransport AmazonSqsTransport(this WolverineOptions endpoints)
    {
        var transports = endpoints.As<WolverineOptions>().Transports;

        return transports.GetOrCreate<AmazonSqsTransport>();
    }

    public static IAmazonSqsTransportConfiguration UseAmazonSqsTransport(this WolverineOptions options, Action<AmazonSQSConfig> configuration)
    {
        var transport = options.AmazonSqsTransport();
        configuration(transport.Config);
        return new AmazonSqlTransportConfiguration(transport, options);
    }


    /// <summary>
    /// Sets up a connection to a locally running Amazon SQS LocalStack
    /// broker for development or testing purposes
    /// </summary>
    /// <param name="port">Port for SQS. Default is 4566</param>
    /// <returns></returns>
    public static IAmazonSqsTransportConfiguration UseAmazonSqsTransportLocally(this WolverineOptions options, int port = 4566)
    {
        var transport = options.AmazonSqsTransport();
        transport.ConnectToLocalStack(port);

        return new AmazonSqlTransportConfiguration(transport, options);
    }
    
    /// <summary>
    ///     Listen for incoming messages at the designated Rabbit MQ queue by name
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="queueName">The name of the Rabbit MQ queue</param>
    /// <param name="configure">
    ///     Optional configuration for this Rabbit Mq queue if being initialized by Wolverine
    ///     <returns></returns>
    public static AmazonSqsListenerConfiguration ListenToSqsQueue(this WolverineOptions endpoints, string queueName, Action<IAmazonSqsListeningEndpoint>? configure = null )
    {
        var transport = endpoints.AmazonSqsTransport();

        var endpoint = transport.EndpointForQueue(queueName);
        endpoint.IsListener = true;
        
        configure?.Invoke(endpoint);

        return new AmazonSqsListenerConfiguration(endpoint);
    }

    public static AmazonSqsSubscriberConfiguration ToSqsQueue(this IPublishToExpression publishing, string queueName)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<AmazonSqsTransport>();

        var endpoint = transport.EndpointForQueue(queueName);
        
        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(endpoint.Uri);

        return new AmazonSqsSubscriberConfiguration(endpoint);
    }

}