using Amazon.Runtime;
using Wolverine.Runtime;

namespace Wolverine.AmazonSqs;

public interface IAmazonSqsTransportConfiguration
{
    /// <summary>
    /// Add credentials for the connection to AWS SQS
    /// </summary>
    /// <param name="credentials"></param>
    /// <returns></returns>
    IAmazonSqsTransportConfiguration Credentials(AWSCredentials credentials);
    
    /// <summary>
    /// Add a credential source for the connection to AWS SQS
    /// </summary>
    /// <param name="credentialSource"></param>
    /// <returns></returns>
    IAmazonSqsTransportConfiguration Credentials(Func<IWolverineRuntime, AWSCredentials> credentialSource);
    
    /// <summary>
    /// All Rabbit MQ exchanges, queues, and bindings should be declared at runtime by Wolverine.
    /// </summary>
    /// <returns></returns>
    IAmazonSqsTransportConfiguration AutoProvision();

    /// <summary>
    /// All queues should be purged of existing messages on first usage
    /// </summary>
    /// <returns></returns>
    IAmazonSqsTransportConfiguration AutoPurgeOnStartup();

    /// <summary>
    /// Direct this application to use a LocalStack connection when
    /// the system is detected to be running with EnvironmentName == "Development"
    /// </summary>
    /// <param name="port">Port to connect to LocalStack. Default is 4566</param>
    /// <returns></returns>
    IAmazonSqsTransportConfiguration UseLocalStackIfDevelopment(int port = 4566);
}