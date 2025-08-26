using Azure.Messaging.ServiceBus.Administration;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Interop.MassTransit;

namespace Wolverine.AzureServiceBus;

public class
    AzureServiceBusQueueListenerConfiguration : ListenerConfiguration<AzureServiceBusQueueListenerConfiguration,
        AzureServiceBusQueue>
{
    public AzureServiceBusQueueListenerConfiguration(AzureServiceBusQueue endpoint) : base(endpoint)
    {
    }
    
        /// <summary>
    /// Configure this Azure Service Bus queue to run exclusively on a single node
    /// with session-based parallel processing. This ensures only one node processes
    /// the queue while allowing multiple sessions to be processed in parallel.
    /// </summary>
    /// <param name="configuration">The Azure Service Bus listener configuration</param>
    /// <param name="maxParallelSessions">Maximum number of sessions to process in parallel. Default is 10.</param>
    /// <param name="endpointName">Optional endpoint name for identification</param>
    /// <returns>The configuration for method chaining</returns>
    public AzureServiceBusQueueListenerConfiguration ExclusiveNodeWithSessions(
        int maxParallelSessions = 10,
        string? endpointName = null)
    {
        // First ensure sessions are required with the specified parallelism
        RequireSessions(maxParallelSessions);
        
        // Then apply exclusive node configuration
        ExclusiveNodeWithSessionOrdering(maxParallelSessions, endpointName);
        
        return this;
    }

    /// <summary>
    ///     Add circuit breaker exception handling to this listener
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AzureServiceBusQueueListenerConfiguration CircuitBreaker(Action<CircuitBreakerOptions>? configure = null)
    {
        add(e =>
        {
            e.CircuitBreakerOptions = new CircuitBreakerOptions();
            configure?.Invoke(e.CircuitBreakerOptions);
        });

        return this;
    }

    /// <summary>
    ///     Configure the underlying Azure Service Bus queue. This is only applicable when
    ///     Wolverine is creating the queues
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AzureServiceBusQueueListenerConfiguration ConfigureQueue(Action<CreateQueueOptions> configure)
    {
        add(e => configure(e.Options));
        return this;
    }

    /// <summary>
    ///     The maximum number of messages to receive in a single batch when listening
    ///     in either buffered or durable modes. The default is 20.
    /// </summary>
    public AzureServiceBusQueueListenerConfiguration MaximumMessagesToReceive(int maximum)
    {
        add(e => e.MaximumMessagesToReceive = maximum);
        return this;
    }

    /// <summary>
    ///     The duration for which the listener waits for a message to arrive in the
    ///     queue before returning. If a message is available, the call returns sooner than this time.
    ///     If no messages are available and the wait time expires, the call returns successfully
    ///     with an empty list of messages. Default is 5 seconds.
    /// </summary>
    public AzureServiceBusQueueListenerConfiguration MaximumWaitTime(TimeSpan time)
    {
        add(e => e.MaximumWaitTime = time);
        return this;
    }

    /// <summary>
    /// Completely disable all SQS dead letter queueing for just this queue
    /// </summary>
    /// <returns></returns>
    public AzureServiceBusQueueListenerConfiguration DisableDeadLetterQueueing()
    {
        add(e => e.DeadLetterQueueName = null);
        return this;
    }

    /// <summary>
    /// Customize the dead letter queueing for just this queue
    /// </summary>
    /// <param name="deadLetterQueue"></param>
    /// <param name="configure">Optionally configure properties of the dead letter queue itself</param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public AzureServiceBusQueueListenerConfiguration ConfigureDeadLetterQueue(string deadLetterQueue,
        Action<AzureServiceBusQueue>? configure = null)
    {
        if (deadLetterQueue == null)
        {
            throw new ArgumentNullException(nameof(deadLetterQueue));
        }

        add(e =>
        {
            e.DeadLetterQueueName = new AzureServiceBusTransport().SanitizeIdentifier(deadLetterQueue);
            if (configure != null)
            {
                e.ConfigureDeadLetterQueue(configure);
            }
        });

        return this;
    }

    /// <summary>
    /// Force this queue to require session identifiers. Use this for FIFO semantics
    /// </summary>
    /// <param name="listenerCount">The maximum number of parallel sessions that can be processed at any one time</param>
    /// <returns></returns>
    public AzureServiceBusQueueListenerConfiguration RequireSessions(int? listenerCount = null)
    {
        add(e =>
        {
            e.Options.RequiresSession = true;
            if (listenerCount.HasValue)
            {
                e.ListenerCount = listenerCount.Value;
            }
        });

        return this;
    }

    /// <summary>
    /// Utilize custom envelope mapping for Amazon Service Bus interoperability with external non-Wolverine systems
    /// </summary>
    /// <param name="mapper"></param>
    /// <returns></returns>
    public AzureServiceBusQueueListenerConfiguration InteropWith(IAzureServiceBusEnvelopeMapper mapper)
    {
        add(e => e.Mapper = mapper);
        return this;
    }

    /// <summary>
    /// Utilize an envelope mapper that is interoperable with MassTransit
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AzureServiceBusQueueListenerConfiguration UseMassTransitInterop(Action<IMassTransitInterop> configure)   
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Use an envelope mapper that is interoperable with NServiceBus
    /// </summary>
    /// <returns></returns>
    public AzureServiceBusQueueListenerConfiguration UseNServiceBusInterop()
    {
        throw new NotImplementedException();
    }
}