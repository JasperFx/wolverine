using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;

namespace Wolverine.AmazonSqs;

public class AmazonSqsListenerConfiguration : ListenerConfiguration<AmazonSqsListenerConfiguration, AmazonSqsQueue>
{
    internal AmazonSqsListenerConfiguration(AmazonSqsQueue queue) : base(queue)
    {
    }
    
    /// <summary>
    /// Add circuit breaker exception handling to this listener
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AmazonSqsListenerConfiguration CircuitBreaker(Action<CircuitBreakerOptions>? configure = null)
    {
        add(e =>
        {
            e.CircuitBreakerOptions = new CircuitBreakerOptions();
            configure?.Invoke(e.CircuitBreakerOptions);
        });

        return this;
    }
    
}