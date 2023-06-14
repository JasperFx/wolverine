using Amazon.Runtime;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Internal;

public class AmazonSqsTransportConfiguration : BrokerExpression<AmazonSqsTransport, AmazonSqsQueue, AmazonSqsQueue,
    AmazonSqsListenerConfiguration, AmazonSqsSubscriberConfiguration, AmazonSqsTransportConfiguration>
{
    public AmazonSqsTransportConfiguration(AmazonSqsTransport transport, WolverineOptions options) : base(transport,
        options)
    {
    }

    protected override AmazonSqsListenerConfiguration createListenerExpression(AmazonSqsQueue listenerEndpoint)
    {
        return new AmazonSqsListenerConfiguration(listenerEndpoint);
    }

    protected override AmazonSqsSubscriberConfiguration createSubscriberExpression(AmazonSqsQueue subscriberEndpoint)
    {
        return new AmazonSqsSubscriberConfiguration(subscriberEndpoint);
    }

    /// <summary>
    ///     Add credentials for the connection to AWS SQS
    /// </summary>
    /// <param name="credentials"></param>
    /// <returns></returns>
    public AmazonSqsTransportConfiguration Credentials(AWSCredentials credentials)
    {
        Transport.CredentialSource = _ => credentials;
        return this;
    }

    /// <summary>
    ///     Add a credential source for the connection to AWS SQS
    /// </summary>
    /// <param name="credentialSource"></param>
    /// <returns></returns>
    public AmazonSqsTransportConfiguration Credentials(Func<IWolverineRuntime, AWSCredentials> credentialSource)
    {
        Transport.CredentialSource = credentialSource;
        return this;
    }

    /// <summary>
    ///     Direct this application to use a LocalStack connection when
    ///     the system is detected to be running with EnvironmentName == "Development"
    /// </summary>
    /// <param name="port">Port to connect to LocalStack. Default is 4566</param>
    /// <returns></returns>
    public AmazonSqsTransportConfiguration UseLocalStackIfDevelopment(int port = 4566)
    {
        Transport.LocalStackPort = port;
        Transport.UseLocalStackInDevelopment = true;
        return this;
    }

    /// <summary>
    ///     Apply a conventional routing topology based on message types
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AmazonSqsTransportConfiguration UseConventionalRouting(
        Action<AmazonSqsMessageRoutingConvention>? configure = null)
    {
        var routing = new AmazonSqsMessageRoutingConvention();
        configure?.Invoke(routing);

        Options.RouteWith(routing);

        return this;
    }
}