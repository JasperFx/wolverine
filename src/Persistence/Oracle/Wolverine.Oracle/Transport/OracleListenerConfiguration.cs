using Wolverine.Configuration;
using Wolverine.ErrorHandling;

namespace Wolverine.Oracle.Transport;

public class OracleListenerConfiguration : ListenerConfiguration<OracleListenerConfiguration, OracleQueue>
{
    public OracleListenerConfiguration(OracleQueue endpoint) : base(endpoint)
    {
    }

    public OracleListenerConfiguration(Func<OracleQueue> source) : base(source)
    {
    }

    /// <summary>
    ///     The maximum number of messages to receive in a single batch when listening
    ///     in either buffered or durable modes. The default is 20.
    /// </summary>
    public OracleListenerConfiguration MaximumMessagesToReceive(int maximum)
    {
        add(e => e.MaximumMessagesToReceive = maximum);
        return this;
    }

    /// <summary>
    ///     Add circuit breaker exception handling to this listener
    /// </summary>
    public OracleListenerConfiguration CircuitBreaker(Action<CircuitBreakerOptions>? configure = null)
    {
        add(e =>
        {
            e.CircuitBreakerOptions = new CircuitBreakerOptions();
            configure?.Invoke(e.CircuitBreakerOptions);
        });

        return this;
    }
}
