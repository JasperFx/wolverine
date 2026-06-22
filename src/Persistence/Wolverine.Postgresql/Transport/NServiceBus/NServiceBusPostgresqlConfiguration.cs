using Wolverine.Configuration;
using Wolverine.ErrorHandling;

namespace Wolverine.Postgresql.Transport.NServiceBus;

public class NServiceBusPostgresqlListenerConfiguration
    : ListenerConfiguration<NServiceBusPostgresqlListenerConfiguration, NServiceBusPostgresqlQueue>
{
    public NServiceBusPostgresqlListenerConfiguration(NServiceBusPostgresqlQueue endpoint) : base(endpoint)
    {
    }

    /// <summary>
    ///     The maximum number of messages to receive in a single poll. Default is 20.
    /// </summary>
    public NServiceBusPostgresqlListenerConfiguration MaximumMessagesToReceive(int maximum)
    {
        add(e => e.MaximumMessagesToReceive = maximum);
        return this;
    }

    /// <summary>
    ///     Configure how often to poll for new messages when the queue is idle.
    /// </summary>
    public NServiceBusPostgresqlListenerConfiguration PollingInterval(TimeSpan interval)
    {
        add(e => e.PollingInterval = interval);
        return this;
    }

    /// <summary>
    ///     The bare NServiceBus queue/table name that foreign endpoints should reply to.
    ///     When not set, the Wolverine endpoint marked <c>UseForReplies()</c> is used.
    /// </summary>
    public NServiceBusPostgresqlListenerConfiguration ReplyQueueName(string queueName)
    {
        add(e => e.InteropReplyQueueName = queueName);
        return this;
    }

    /// <summary>
    ///     Add circuit breaker exception handling to this listener.
    /// </summary>
    public NServiceBusPostgresqlListenerConfiguration CircuitBreaker(Action<CircuitBreakerOptions>? configure = null)
    {
        add(e =>
        {
            e.CircuitBreakerOptions = new CircuitBreakerOptions();
            configure?.Invoke(e.CircuitBreakerOptions);
        });
        return this;
    }
}

public class NServiceBusPostgresqlSubscriberConfiguration
    : SubscriberConfiguration<NServiceBusPostgresqlSubscriberConfiguration, NServiceBusPostgresqlQueue>
{
    public NServiceBusPostgresqlSubscriberConfiguration(NServiceBusPostgresqlQueue endpoint) : base(endpoint)
    {
    }

    /// <summary>
    ///     The bare NServiceBus queue/table name that the receiving endpoint should reply to.
    ///     When not set, the Wolverine endpoint marked <c>UseForReplies()</c> is used.
    /// </summary>
    public NServiceBusPostgresqlSubscriberConfiguration ReplyQueueName(string queueName)
    {
        add(e => e.InteropReplyQueueName = queueName);
        return this;
    }
}
