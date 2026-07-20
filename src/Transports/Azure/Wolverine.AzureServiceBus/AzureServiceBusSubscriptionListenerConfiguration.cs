using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Interop.MassTransit;

namespace Wolverine.AzureServiceBus;

public class AzureServiceBusSubscriptionListenerConfiguration : InteroperableListenerConfiguration<AzureServiceBusSubscriptionListenerConfiguration,
    AzureServiceBusSubscription, IAzureServiceBusEnvelopeMapper, AzureServiceBusEnvelopeMapper>
{
    public AzureServiceBusSubscriptionListenerConfiguration(AzureServiceBusSubscription endpoint) : base(endpoint)
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
    public AzureServiceBusSubscriptionListenerConfiguration ExclusiveNodeWithSessions(
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
    public AzureServiceBusSubscriptionListenerConfiguration CircuitBreaker(Action<CircuitBreakerOptions>? configure = null)
    {
        add(e =>
        {
            e.CircuitBreakerOptions = new CircuitBreakerOptions();
            configure?.Invoke(e.CircuitBreakerOptions);
        });

        return this;
    }

    /// <summary>
    ///     Configure the underlying Azure Service Bus Subscription. This is only applicable when
    ///     Wolverine is creating the Subscriptions
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AzureServiceBusSubscriptionListenerConfiguration ConfigureSubscription(Action<CreateSubscriptionOptions> configure)
    {
        add(e => configure(e.Options));
        return this;
    }

    /// <summary>
    ///     Customize the Azure Service Bus <see cref="ServiceBusProcessorOptions" /> used by this
    ///     subscription listener when running in the inline (<c>ProcessInline()</c>) mode. This is the way
    ///     to raise <see cref="ServiceBusProcessorOptions.MaxAutoLockRenewalDuration" /> for inline handlers
    ///     that run longer than the Azure SDK's default of five minutes. Wolverine reserves control of the
    ///     properties it depends on for message acknowledgement (currently <c>ReceiveMode</c>), which are
    ///     re-asserted after this action runs.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AzureServiceBusSubscriptionListenerConfiguration ConfigureProcessor(Action<ServiceBusProcessorOptions> configure)
    {
        add(e => e.ConfigureProcessor = configure);
        return this;
    }

    /// <summary>
    ///     Customize the Azure Service Bus <see cref="ServiceBusSessionProcessorOptions" /> used by this
    ///     session-enabled subscription listener — e.g. <c>MaxConcurrentSessions</c>,
    ///     <c>MaxAutoLockRenewalDuration</c>, <c>SessionIdleTimeout</c>, or <c>SessionIds</c>. Calling this
    ///     implies <see cref="RequireSessions" /> and switches the session listener from the default
    ///     AcceptNextSession loop to a <see cref="ServiceBusSessionProcessor" />. Multiple calls compose.
    ///     Wolverine reserves control of the properties it depends on for message acknowledgement
    ///     (<c>ReceiveMode</c>, <c>AutoCompleteMessages</c>), which are re-asserted after this action runs.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AzureServiceBusSubscriptionListenerConfiguration ConfigureSessionProcessor(
        Action<ServiceBusSessionProcessorOptions> configure)
    {
        add(e =>
        {
            e.Options.RequiresSession = true;
            // Compose rather than overwrite so the SessionIds sugar can coexist with an explicit hook
            e.ConfigureSessionProcessor += configure;
        });
        return this;
    }

    /// <summary>
    ///     Pin this listener to only the given session identifiers. On a shared subscription this turns the
    ///     session id into a broker-enforced routing key: competing consumers each pinned to their own id(s)
    ///     never see each other's messages. Producers select the target by setting <c>DeliveryOptions.GroupId</c>
    ///     to the session id. Delegates to <see cref="ConfigureSessionProcessor" /> by populating
    ///     <c>ServiceBusSessionProcessorOptions.SessionIds</c>. (GH-3533)
    /// </summary>
    /// <param name="identifiers">The session identifiers this listener should exclusively lock</param>
    /// <returns></returns>
    public AzureServiceBusSubscriptionListenerConfiguration RequireSessionsWithOnlyTheseIdentifiers(
        params string[] identifiers)
    {
        RequireSessions();
        return ConfigureSessionProcessor(options =>
        {
            foreach (var id in identifiers)
            {
                options.SessionIds.Add(id);
            }
        });
    }

    /// <summary>
    ///     Configure the underlying Azure Service Bus Subscription rule. This is only applicable when
    ///     Wolverine is creating the Subscription.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AzureServiceBusSubscriptionListenerConfiguration ConfigureSubscriptionRule(Action<CreateRuleOptions> configure)
    {
        add(e => configure(e.RuleOptions));
        return this;
    }

    /// <summary>
    ///     Configure the underlying Azure Service Bus Subscription. This is only applicable when
    ///     Wolverine is creating the Subscriptions
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AzureServiceBusSubscriptionListenerConfiguration ConfigureTopic(Action<CreateTopicOptions> configure)
    {
        add(e => configure(e.Topic.Options));
        return this;
    }

    /// <summary>
    ///     The maximum number of messages to receive in a single batch when listening
    ///     in either buffered or durable modes. The default is 20.
    /// </summary>
    public AzureServiceBusSubscriptionListenerConfiguration MaximumMessagesToReceive(int maximum)
    {
        add(e => e.MaximumMessagesToReceive = maximum);
        return this;
    }

    /// <summary>
    ///     The duration for which the listener waits for a message to arrive in the
    ///     Subscription before returning. If a message is available, the call returns sooner than this time.
    ///     If no messages are available and the wait time expires, the call returns successfully
    ///     with an empty list of messages. Default is 5 seconds.
    /// </summary>
    public AzureServiceBusSubscriptionListenerConfiguration MaximumWaitTime(TimeSpan time)
    {
        add(e => e.MaximumWaitTime = time);
        return this;
    }

    /// <summary>
    ///     The number of messages that the underlying Azure Service Bus receiver eagerly buffers
    ///     on the client ahead of processing for this subscription. The default is 0 (prefetch is
    ///     disabled), or the transport-wide default set through
    ///     <c>UseAzureServiceBus(...).PrefetchCount()</c>. Prefetched messages age against the
    ///     subscription's message lock duration while they sit in the client buffer, so size this
    ///     relative to MaximumMessagesToReceive and your handler latency
    /// </summary>
    /// <param name="prefetchCount">The client-side prefetch count. Must be non-negative</param>
    /// <returns></returns>
    public AzureServiceBusSubscriptionListenerConfiguration PrefetchCount(int prefetchCount)
    {
        if (prefetchCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(prefetchCount), prefetchCount,
                "PrefetchCount cannot be negative");
        }

        add(e => e.PrefetchCount = prefetchCount);
        return this;
    }

    /// <summary>
    /// Force this subscription listener to require session identifiers. Use this for FIFO semantics
    /// </summary>
    /// <param name="listenerCount">The maximum number of parallel sessions that can be processed at any one time</param>
    /// <returns></returns>
    public AzureServiceBusSubscriptionListenerConfiguration RequireSessions(int? listenerCount = null)
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
    public AzureServiceBusSubscriptionListenerConfiguration InteropWith(IAzureServiceBusEnvelopeMapper mapper)
    {
        add(e => e.EnvelopeMapper = mapper);
        return this;
    }

    /// <summary>
    /// Utilize an envelope mapper that is interoperable with MassTransit
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AzureServiceBusSubscriptionListenerConfiguration UseMassTransitInterop(Action<IMassTransitInterop>? configure = null)
    {
        add(e => e.UseMassTransitInterop(configure));
        return this;
    }

    /// <summary>
    /// Use an envelope mapper that is interoperable with NServiceBus
    /// </summary>
    /// <returns></returns>
    public AzureServiceBusSubscriptionListenerConfiguration UseNServiceBusInterop()
    {
        add(e => e.UseNServiceBusInterop());
        return this;
    }
}