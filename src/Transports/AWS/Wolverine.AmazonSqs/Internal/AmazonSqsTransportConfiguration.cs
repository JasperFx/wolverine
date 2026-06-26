using Amazon.Runtime;
using Microsoft.Extensions.DependencyInjection;
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
    ///     Apply a conventional routing topology with the specified naming source.
    ///     Using <see cref="NamingSource.FromHandlerType"/> is appropriate for modular monolith
    ///     scenarios where you have more than one handler for a given message type.
    /// </summary>
    /// <param name="namingSource"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AmazonSqsTransportConfiguration UseConventionalRouting(NamingSource namingSource,
        Action<AmazonSqsMessageRoutingConvention>? configure = null)
    {
        var routing = new AmazonSqsMessageRoutingConvention();
        routing.UseNaming(namingSource);
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
    /// Set a transport-wide default name for the dead-letter queue used by every SQS listener
    /// that hasn't been individually configured with
    /// <c>AmazonSqsListenerConfiguration.ConfigureDeadLetterQueue(...)</c> or
    /// <c>DisableDeadLetterQueueing()</c>. Useful with multi-environment AWS accounts where
    /// the default <c>"wolverine-dead-letter-queue"</c> name would collide across environments,
    /// or with conventional routing / auto-provisioning where touching every listener
    /// individually is impractical.
    ///
    /// Resolution order (per listener):
    /// <list type="number">
    ///   <item>Per-listener <c>ConfigureDeadLetterQueue("name")</c> wins.</item>
    ///   <item>Per-listener <c>DisableDeadLetterQueueing()</c> wins.</item>
    ///   <item>Otherwise, this transport-wide default is used.</item>
    /// </list>
    ///
    /// <c>DisableAllNativeDeadLetterQueues()</c> disables the entire SQS DLQ surface regardless
    /// of what's configured here. The supplied name is sanitized via
    /// <see cref="AmazonSqsTransport.SanitizeSqsName"/> so periods and other illegal SQS
    /// characters are normalised consistently with per-listener configuration.
    /// </summary>
    /// <param name="deadLetterQueueName">
    /// Default DLQ name to apply across the transport. Must be non-null and non-empty;
    /// pass to <c>DisableAllNativeDeadLetterQueues()</c> instead if you want to turn the
    /// surface off entirely.
    /// </param>
    /// <returns></returns>
    public AmazonSqsTransportConfiguration DefaultDeadLetterQueueName(string deadLetterQueueName)
    {
        if (string.IsNullOrWhiteSpace(deadLetterQueueName))
        {
            throw new ArgumentException(
                "Dead-letter queue name must be a non-empty value. " +
                $"Call {nameof(DisableAllNativeDeadLetterQueues)}() to disable the SQS DLQ surface globally.",
                nameof(deadLetterQueueName));
        }

        Transport.DefaultDeadLetterQueueName =
            AmazonSqsTransport.SanitizeSqsName(deadLetterQueueName);
        return this;
    }

    /// <summary>
    /// Enable a background listener that drains the native Amazon SQS dead letter queue(s) and
    /// recovers the messages into Wolverine's durable dead letter storage (the
    /// <c>wolverine_dead_letters</c> table), making natively dead-lettered messages queryable and
    /// replayable through <c>IDeadLetters</c> and tools like CritterWatch. This is the SQS analogue
    /// of RabbitMQ's <c>EnableDeadLetterQueueRecovery()</c>.
    ///
    /// With no arguments, every distinct dead letter queue used by a listening SQS queue is drained.
    /// Requires Wolverine's durable message storage (a database) to be configured.
    /// </summary>
    /// <returns></returns>
    public AmazonSqsTransportConfiguration EnableDeadLetterQueueRecovery()
    {
        ensureRecoveryServicesRegistered();
        return this;
    }

    /// <summary>
    /// Enable a background listener that drains the named Amazon SQS dead letter queue(s) and
    /// recovers the messages into Wolverine's durable dead letter storage. Use this overload when
    /// the dead letter queues you want to recover from are not directly attached to a Wolverine
    /// listener (for example, queues fed by an SQS native redrive policy you manage yourself).
    /// </summary>
    /// <param name="deadLetterQueueNames">The names of the SQS dead letter queues to drain.</param>
    /// <returns></returns>
    public AmazonSqsTransportConfiguration EnableDeadLetterQueueRecovery(params string[] deadLetterQueueNames)
    {
        var settings = ensureRecoveryServicesRegistered();
        foreach (var name in deadLetterQueueNames)
        {
            var sanitized = AmazonSqsTransport.SanitizeSqsName(name);
            if (!settings.QueueNames.Contains(sanitized))
            {
                settings.QueueNames.Add(sanitized);
            }
        }

        return this;
    }

    private AmazonSqsDeadLetterQueueRecoverySettings ensureRecoveryServicesRegistered()
    {
        var existing = Options.Services
            .Where(s => s.ServiceType == typeof(AmazonSqsDeadLetterQueueRecoverySettings))
            .Select(s => s.ImplementationInstance)
            .OfType<AmazonSqsDeadLetterQueueRecoverySettings>()
            .FirstOrDefault();

        if (existing != null)
        {
            return existing;
        }

        var settings = new AmazonSqsDeadLetterQueueRecoverySettings();
        Options.Services.AddSingleton(settings);
        Options.Services.AddSingleton(Transport);
        Options.Services.AddHostedService<SqsDeadLetterQueueListener>();
        return settings;
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

        // Lowercase to match URI normalization (see tryBuildSystemEndpoints comment). In Solo mode the
        // assigned node number is always 1 (#3188), so key the per-node control queue on the unique
        // node id to keep multiple Solo hosts on one broker from colliding. See #3189.
        var controlNode = Options.Durability.Mode == DurabilityMode.Solo
            ? Options.UniqueNodeId.ToString("N")
            : Options.Durability.AssignedNodeNumber.ToString();
        var queueName = AmazonSqsTransport.SanitizeSqsName(
            "wolverine.control." + controlNode)
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