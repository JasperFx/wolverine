using Amazon.Runtime;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AmazonSns.Internal;

public class AmazonSnsTransportConfiguration : BrokerExpression<AmazonSnsTransport, AmazonSnsTopic, AmazonSnsTopic,
    AmazonSnsListenerConfiguration, AmazonSnsSubscriberConfiguration, AmazonSnsTransportConfiguration>
{
    public AmazonSnsTransportConfiguration(AmazonSnsTransport transport, WolverineOptions options) : base(transport,
        options)
    {
    }

    protected override AmazonSnsListenerConfiguration createListenerExpression(AmazonSnsTopic listenerEndpoint)
    {
        return new AmazonSnsListenerConfiguration(listenerEndpoint);
    }

    protected override AmazonSnsSubscriberConfiguration createSubscriberExpression(AmazonSnsTopic subscriberEndpoint)
    {
        return new AmazonSnsSubscriberConfiguration(subscriberEndpoint);
    }

    /// <summary>
    ///     Add credentials for the connection to AWS SQS
    /// </summary>
    /// <param name="credentials"></param>
    /// <returns></returns>
    public AmazonSnsTransportConfiguration Credentials(AWSCredentials credentials)
    {
        Transport.CredentialSource = _ => credentials;
        return this;
    }

    /// <summary>
    ///     Add a credential source for the connection to AWS SQS
    /// </summary>
    /// <param name="credentialSource"></param>
    /// <returns></returns>
    public AmazonSnsTransportConfiguration Credentials(Func<IWolverineRuntime, AWSCredentials> credentialSource)
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
    public AmazonSnsTransportConfiguration UseLocalStackIfDevelopment(int port = 4566)
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
    public AmazonSnsTransportConfiguration UseConventionalRouting(
        Action<AmazonSnsMessageRoutingConvention>? configure = null)
    {
        var routing = new AmazonSnsMessageRoutingConvention();
        configure?.Invoke(routing);

        Options.RouteWith(routing);

        return this;
    }
}
