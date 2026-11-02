using Wolverine.Configuration;
using Wolverine.ErrorHandling;

namespace Wolverine.MySql.Transport;

public class MySqlListenerConfiguration : ListenerConfiguration<MySqlListenerConfiguration, MySqlQueue>
{
    public MySqlListenerConfiguration(MySqlQueue endpoint) : base(endpoint)
    {
    }

    public MySqlListenerConfiguration(Func<MySqlQueue> source) : base(source)
    {
    }

    /// <summary>
    ///     The maximum number of messages to receive in a single batch when listening
    ///     in either buffered or durable modes. The default is 20.
    /// </summary>
    public MySqlListenerConfiguration MaximumMessagesToReceive(int maximum)
    {
        add(e => e.MaximumMessagesToReceive = maximum);
        return this;
    }

    /// <summary>
    ///     Configure how often to poll for new messages when the queue is idle.
    ///     If not set, falls back to DurabilitySettings.ScheduledJobPollingTime (default 5s).
    /// </summary>
    public MySqlListenerConfiguration PollingInterval(TimeSpan interval)
    {
        add(e => e.PollingInterval = interval);
        return this;
    }

    /// <summary>
    ///     Add circuit breaker exception handling to this listener
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public MySqlListenerConfiguration CircuitBreaker(Action<CircuitBreakerOptions>? configure = null)
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
    public MySqlListenerConfiguration ExternallyOwned()
    {
        // Set at once as well as when the endpoint compiles: the queue tables are registered with the
        // message store's schema early in startup, before any endpoint is compiled
        if (_endpoint != null) _endpoint.IsExternallyOwned = true;

        add(e => e.IsExternallyOwned = true);
        return this;
    }
}
