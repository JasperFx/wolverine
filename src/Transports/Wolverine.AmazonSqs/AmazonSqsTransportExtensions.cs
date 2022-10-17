using Amazon.Runtime;
using Amazon.SQS;
using Baseline;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Runtime;

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
        return transport;
    }


    /// <summary>
    /// Sets up a connection to a locally running Amazon SQS LocalStack
    /// broker for development or testing purposes
    /// </summary>
    /// <param name="port">Port for SQS. Default is 4566</param>
    /// <returns></returns>
    public static IAmazonSqsTransportConfiguration UseAmazonSqsTransportLocally(this WolverineOptions options, int port = 4566)
    {
        return options.UseAmazonSqsTransport(config => config.ServiceURL = $"http://localhost:{port}")
            .Credentials(new BasicAWSCredentials("ignore", "ignore"));
    }
}

public interface IAmazonSqsTransportConfiguration
{
    IAmazonSqsTransportConfiguration Credentials(AWSCredentials credentials);
    IAmazonSqsTransportConfiguration Credentials(Func<IWolverineRuntime, AWSCredentials> credentialSource);
    
    
}