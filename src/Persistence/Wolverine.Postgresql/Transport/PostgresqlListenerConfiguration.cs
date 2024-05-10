using Wolverine.Configuration;
using Wolverine.ErrorHandling;

namespace Wolverine.Postgresql.Transport;

public class PostgresqlListenerConfiguration : ListenerConfiguration<PostgresqlListenerConfiguration, PostgresqlQueue>
{
    public PostgresqlListenerConfiguration(PostgresqlQueue endpoint) : base(endpoint)
    {
    }

    public PostgresqlListenerConfiguration(Func<PostgresqlQueue> source) : base(source)
    {
    }

    /// <summary>
    ///     The maximum number of messages to receive in a single batch when listening
    ///     in either buffered or durable modes. The default is 20.
    /// </summary>
    public PostgresqlListenerConfiguration MaximumMessagesToReceive(int maximum)
    {
        add(e => e.MaximumMessagesToReceive = maximum);
        return this;
    }

    /// <summary>
    ///     Add circuit breaker exception handling to this listener
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public PostgresqlListenerConfiguration CircuitBreaker(Action<CircuitBreakerOptions>? configure = null)
    {
        add(e =>
        {
            e.CircuitBreakerOptions = new CircuitBreakerOptions();
            configure?.Invoke(e.CircuitBreakerOptions);
        });

        return this;
    }
}