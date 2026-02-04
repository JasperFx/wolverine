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
}
