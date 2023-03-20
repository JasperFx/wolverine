using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Transports.Postgresql.Internal;

namespace Wolverine.Transports.Postgresql;

public class PostgresQueueListenerConfiguration
    : ListenerConfiguration<PostgresQueueListenerConfiguration, PostgresQueue>
{
    public PostgresQueueListenerConfiguration(PostgresQueue endpoint) : base(endpoint)
    {
    }

    /// <summary>
    ///     Add circuit breaker exception handling to this listener
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public PostgresQueueListenerConfiguration CircuitBreaker(
        Action<CircuitBreakerOptions>? configure = null)
    {
        add(e =>
        {
            e.CircuitBreakerOptions = new CircuitBreakerOptions();
            configure?.Invoke(e.CircuitBreakerOptions);
        });

        return this;
    }

    
    /// <summary>
    ///     The maximum number of messages to receive in a single batch when listening
    ///     in either buffered or durable modes. The default is 20.
    /// </summary>
    public PostgresQueueListenerConfiguration MaximumConcurrentMessages(int maximum)
    {
        add(e => e.MaximumConcurrentMessages = maximum);
        return this;
    }

}
