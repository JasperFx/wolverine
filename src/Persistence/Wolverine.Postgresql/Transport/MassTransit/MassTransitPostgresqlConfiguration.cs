using Wolverine.Configuration;
using Wolverine.ErrorHandling;

namespace Wolverine.Postgresql.Transport.MassTransit;

public class MassTransitPostgresqlListenerConfiguration
    : ListenerConfiguration<MassTransitPostgresqlListenerConfiguration, MassTransitPostgresqlQueue>
{
    public MassTransitPostgresqlListenerConfiguration(MassTransitPostgresqlQueue endpoint) : base(endpoint)
    {
    }

    /// <summary>
    ///     The maximum number of messages to receive in a single poll. Default is 20.
    /// </summary>
    public MassTransitPostgresqlListenerConfiguration MaximumMessagesToReceive(int maximum)
    {
        add(e => e.MaximumMessagesToReceive = maximum);
        return this;
    }

    /// <summary>
    ///     Configure how often to poll for new messages when the queue is idle.
    /// </summary>
    public MassTransitPostgresqlListenerConfiguration PollingInterval(TimeSpan interval)
    {
        add(e => e.PollingInterval = interval);
        return this;
    }

    /// <summary>
    ///     How long a fetched message is leased before MassTransit re-delivers it. Default 5 minutes.
    /// </summary>
    public MassTransitPostgresqlListenerConfiguration LockDuration(TimeSpan duration)
    {
        add(e => e.LockDuration = duration);
        return this;
    }

    /// <summary>
    ///     The bare MassTransit queue name that foreign endpoints should reply to.
    ///     When not set, the Wolverine endpoint marked <c>UseForReplies()</c> is used.
    /// </summary>
    public MassTransitPostgresqlListenerConfiguration ReplyQueueName(string queueName)
    {
        add(e => e.InteropReplyQueueName = queueName);
        return this;
    }

    /// <summary>
    ///     Add circuit breaker exception handling to this listener.
    /// </summary>
    public MassTransitPostgresqlListenerConfiguration CircuitBreaker(Action<CircuitBreakerOptions>? configure = null)
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
    public MassTransitPostgresqlListenerConfiguration ExternallyOwned()
    {
        // Set at once as well as when the endpoint compiles: the queue tables are registered with the
        // message store's schema early in startup, before any endpoint is compiled
        if (_endpoint != null) _endpoint.IsExternallyOwned = true;

        add(e => e.IsExternallyOwned = true);
        return this;
    }
}

public class MassTransitPostgresqlSubscriberConfiguration
    : SubscriberConfiguration<MassTransitPostgresqlSubscriberConfiguration, MassTransitPostgresqlQueue>
{
    public MassTransitPostgresqlSubscriberConfiguration(MassTransitPostgresqlQueue endpoint) : base(endpoint)
    {
    }

    /// <summary>
    ///     The bare MassTransit queue name that the receiving endpoint should reply to.
    ///     When not set, the Wolverine endpoint marked <c>UseForReplies()</c> is used.
    /// </summary>
    public MassTransitPostgresqlSubscriberConfiguration ReplyQueueName(string queueName)
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
    public MassTransitPostgresqlSubscriberConfiguration ExternallyOwned()
    {
        // Set at once as well as when the endpoint compiles: the queue tables are registered with the
        // message store's schema early in startup, before any endpoint is compiled
        if (_endpoint != null) _endpoint.IsExternallyOwned = true;

        add(e => e.IsExternallyOwned = true);
        return this;
    }
}
