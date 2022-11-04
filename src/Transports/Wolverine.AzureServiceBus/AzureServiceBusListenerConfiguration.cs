using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;

namespace Wolverine.AzureServiceBus;

public class AzureServiceBusListenerConfiguration : ListenerConfiguration<AzureServiceBusListenerConfiguration, AzureServiceBusEndpoint>
{
    public AzureServiceBusListenerConfiguration(AzureServiceBusEndpoint queue) : base(queue)
    {
    }
    
    /// <summary>
    /// Add circuit breaker exception handling to this listener
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AzureServiceBusListenerConfiguration CircuitBreaker(Action<CircuitBreakerOptions>? configure = null)
    {
        add(e =>
        {
            e.CircuitBreakerOptions = new CircuitBreakerOptions();
            configure?.Invoke(e.CircuitBreakerOptions);
        });

        return this;
    }
}