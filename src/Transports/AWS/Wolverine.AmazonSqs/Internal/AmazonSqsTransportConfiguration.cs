using Amazon.Runtime;
using Wolverine.Configuration;
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

    /// <summary>
    /// Globally disable all native dead letter queueing with AWS SQS queues within this entire
    /// application
    /// </summary>
    /// <returns></returns>
    public AmazonSqsTransportConfiguration DisableAllNativeDeadLetterQueues()
    {
        Transport.DisableDeadLetterQueues = true;
        return this;
    }

    /// <summary>
    /// Enable Wolverine system queues for request/reply support.
    /// Creates a per-node response queue that is automatically cleaned up.
    /// </summary>
    /// <returns></returns>
    public AmazonSqsTransportConfiguration EnableSystemQueues()
    {
        Transport.SystemQueuesEnabled = true;
        return this;
    }

    /// <summary>
    /// Control whether Wolverine creates system queues for responses and retries.
    /// Should be set to false if the application lacks permissions to create queues.
    /// </summary>
    /// <param name="enabled"></param>
    /// <returns></returns>
    public AmazonSqsTransportConfiguration SystemQueuesAreEnabled(bool enabled)
    {
        Transport.SystemQueuesEnabled = enabled;
        return this;
    }

    /// <summary>
    /// Enable Wolverine control queues for inter-node communication
    /// (leader election, node coordination).
    /// </summary>
    /// <returns></returns>
    public AmazonSqsTransportConfiguration EnableWolverineControlQueues()
    {
        Transport.SystemQueuesEnabled = true;

        // Lowercase to match URI normalization (see tryBuildSystemEndpoints comment)
        var queueName = AmazonSqsTransport.SanitizeSqsName(
            "wolverine.control." + Options.Durability.AssignedNodeNumber)
            .ToLowerInvariant();

        var queue = Transport.Queues[queueName];
        queue.Mode = EndpointMode.BufferedInMemory;
        queue.IsListener = true;
        queue.EndpointName = "Control";
        queue.IsUsedForReplies = true;
        queue.Role = EndpointRole.System;
        queue.DeadLetterQueueName = null;
        queue.Configuration.Attributes ??= new Dictionary<string, string>();
        queue.Configuration.Attributes["MessageRetentionPeriod"] = "300";

        Options.Transports.NodeControlEndpoint = queue;

        Transport.SystemQueues.Add(queue);

        return this;
    }
}