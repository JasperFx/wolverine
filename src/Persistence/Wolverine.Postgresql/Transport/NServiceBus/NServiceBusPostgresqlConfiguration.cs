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

    /// <summary>
    ///     Mark this queue as owned by another system. Wolverine will not create, migrate or drop its queue and
    ///     scheduled message tables at startup, in <c>resources setup</c> or <c>resources teardown</c>, or as part
    ///     of the message store's schema, even when <c>AutoProvision()</c> is on. <c>resources check</c> still
    ///     checks it. GH-5342.
    /// </summary>
    public NServiceBusPostgresqlListenerConfiguration ExternallyOwned()
    {
        // Set at once as well as when the endpoint compiles: the queue tables are registered with the
        // message store's schema early in startup, before any endpoint is compiled
        if (_endpoint != null) _endpoint.IsExternallyOwned = true;

        add(e => e.IsExternallyOwned = true);
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

    /// <summary>
    ///     Mark this queue as owned by another system. Wolverine will not create, migrate or drop its queue and
    ///     scheduled message tables at startup, in <c>resources setup</c> or <c>resources teardown</c>, or as part
    ///     of the message store's schema, even when <c>AutoProvision()</c> is on. <c>resources check</c> still
    ///     checks it. GH-5342.
    /// </summary>
    public NServiceBusPostgresqlSubscriberConfiguration ExternallyOwned()
    {
        // Set at once as well as when the endpoint compiles: the queue tables are registered with the
        // message store's schema early in startup, before any endpoint is compiled
        if (_endpoint != null) _endpoint.IsExternallyOwned = true;

        add(e => e.IsExternallyOwned = true);
        return this;
    }
}
