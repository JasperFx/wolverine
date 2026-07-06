using Amazon.SimpleNotificationService;
using Amazon.SQS;
using JasperFx.Core.Reflection;
using Wolverine.AmazonSns.Internal;
using Wolverine.AmazonSqs.Internal;
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
    internal static AmazonSnsTransport AmazonSnsTransport(this WolverineOptions endpoints, BrokerName? name = null)
    {
        var transports = endpoints.As<WolverineOptions>().Transports;

        var transport = transports.GetOrCreate<AmazonSnsTransport>(name);

        if (name == null)
        {
            // Default broker: pair the SNS-side SQS client + subscription-queue provisioning with the shared
            // default SQS transport so both target the same account/region and share the user's SQS config.
            transport.SQS ??= transports.GetOrCreate<AmazonSqsTransport>();
        }
        else if (transport.SQS == null)
        {
            // Named broker (GH-3305): the SNS transport holds this broker's Protocol key (== the name), so its
            // topic URIs are "{name}://topic". A paired SQS transport registered under the SAME key would evict the
            // SNS from the TransportCollection (which keys transports by Protocol). Since the paired SQS is only used
            // internally for the SNS-side client + SNS->SQS subscription provisioning (never as a registered
            // listener/sender surface), give it a standalone, unregistered transport that ConnectAsync seeds from the
            // SNS connection so it targets the same account/region. A user who also wants to LISTEN on the
            // subscribed queue does so through a separate (default or differently-named) SQS broker.
            transport.SQS = new AmazonSqsTransport();
            transport.PairedSqsIsStandalone = true;
        }

        return transport;
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
    
    public static AmazonSnsTransportConfiguration UseAmazonSnsTransport(this WolverineOptions options,
        Action<AmazonSimpleNotificationServiceConfig, AmazonSQSConfig> configuration)
    {
        var transport = options.AmazonSnsTransport();
        configuration(transport.SnsConfig, transport.SQS.Config);
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
    /// Add an additional, named broker connection to Amazon SNS
    /// </summary>
    /// <param name="options"></param>
    /// <param name="name"></param>
    /// <returns></returns>
    public static AmazonSnsTransportConfiguration AddNamedAmazonSnsBroker(this WolverineOptions options, BrokerName name)
    {
        var transport = options.AmazonSnsTransport(name);
        return new AmazonSnsTransportConfiguration(transport, options);
    }

    /// <summary>
    /// Add an additional, named broker connection to Amazon SNS
    /// </summary>
    /// <param name="options"></param>
    /// <param name="name"></param>
    /// <param name="configuration"></param>
    /// <returns></returns>
    public static AmazonSnsTransportConfiguration AddNamedAmazonSnsBroker(this WolverineOptions options, BrokerName name,
        Action<AmazonSimpleNotificationServiceConfig> configuration)
    {
        var transport = options.AmazonSnsTransport(name);
        configuration(transport.SnsConfig);
        return new AmazonSnsTransportConfiguration(transport, options);
    }

    /// <summary>
    ///     Sets up a connection to a locally running Amazon SNS LocalStack broker as a named broker
    ///     for development or testing purposes
    /// </summary>
    /// <param name="name">The name of the additional broker</param>
    /// <param name="port">Port for SNS. Default is 4566</param>
    /// <returns></returns>
    public static AmazonSnsTransportConfiguration UseAmazonSnsTransportLocallyAsNamedBroker(this WolverineOptions options,
        BrokerName name, int port = 4566)
    {
        var transport = options.AmazonSnsTransport(name);

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

    /// <summary>
    ///     Publish matching messages to AWS SNS using the topic name on a specific, named broker
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="name">The name of the additional Amazon SNS broker</param>
    /// <param name="topicName">The name of the AWS SNS topic</param>
    /// <returns></returns>
    public static AmazonSnsSubscriberConfiguration ToSnsTopicOnNamedBroker(this IPublishToExpression publishing,
        BrokerName name, string topicName)
    {
        var transport = publishing.As<PublishingExpression>().Parent.AmazonSnsTransport(name);

        var corrected = transport.MaybeCorrectName(topicName);

        var endpoint = transport.EndpointForTopic(corrected);
        endpoint.EndpointName = topicName;

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(endpoint.Uri);

        return new AmazonSnsSubscriberConfiguration(endpoint);
    }
}
