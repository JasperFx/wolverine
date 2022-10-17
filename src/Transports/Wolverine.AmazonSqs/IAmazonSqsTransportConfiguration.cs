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
}