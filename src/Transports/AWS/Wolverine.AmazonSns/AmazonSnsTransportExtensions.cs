using Amazon.SimpleNotificationService;
using JasperFx.Core.Reflection;
using Wolverine.AmazonSns.Internal;
using Wolverine.Configuration;

namespace Wolverine.AmazonSns;

public static class AmazonSnsTransportExtensions
{
    /// <summary>
    ///     Quick access to the Amazon SNS Transport within this application.
    ///     This is for advanced usage
    /// </summary>
    /// <param name="endpoints"></param>
    /// <returns></returns>
    internal static AmazonSnsTransport AmazonSnsTransport(this WolverineOptions endpoints)
    {
        var transports = endpoints.As<WolverineOptions>().Transports;

        return transports.GetOrCreate<AmazonSnsTransport>();
    }

    /// <summary>
    /// Apply additive configuration to the AWS SNS integration within this Wolverine application
    /// </summary>
    /// <param name="options"></param>
    /// <returns></returns>
    public static AmazonSnsTransportConfiguration ConfigureAmazonSnsTransport(this WolverineOptions options)
    {
        var transport = options.AmazonSnsTransport();
        return new AmazonSnsTransportConfiguration(transport, options);
    }

    public static AmazonSnsTransportConfiguration UseAmazonSnsTransport(this WolverineOptions options)
    {
        var transport = options.AmazonSnsTransport();
        return new AmazonSnsTransportConfiguration(transport, options);
    }

    public static AmazonSnsTransportConfiguration UseAmazonSnsTransport(this WolverineOptions options,
        Action<AmazonSimpleNotificationServiceConfig> configuration)
    {
        var transport = options.AmazonSnsTransport();
        configuration(transport.SnsConfig);
        return new AmazonSnsTransportConfiguration(transport, options);
    }

    /// <summary>
    ///     Sets up a connection to a locally running Amazon SNS LocalStack
    ///     broker for development or testing purposes
    /// </summary>
    /// <param name="port">Port for SNS. Default is 4566</param>
    /// <returns></returns>
    public static AmazonSnsTransportConfiguration UseAmazonSnsTransportLocally(this WolverineOptions options,
        int port = 4566)
    {
        var transport = options.AmazonSnsTransport();

        transport.ConnectToLocalStack(port);

        return new AmazonSnsTransportConfiguration(transport, options);
    }

    /// <summary>
    ///     Publish matching messages to AWS SNS using the topic name
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="topicName">The name of the AWS SNS topic</param>
    /// <returns></returns>
    public static AmazonSnsSubscriberConfiguration ToSnsTopic(this IPublishToExpression publishing, string topicName)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<AmazonSnsTransport>();

        var corrected = transport.MaybeCorrectName(topicName);

        var endpoint = transport.EndpointForTopic(corrected);
        endpoint.EndpointName = topicName;

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(endpoint.Uri);

        return new AmazonSnsSubscriberConfiguration(endpoint);
    }
}
