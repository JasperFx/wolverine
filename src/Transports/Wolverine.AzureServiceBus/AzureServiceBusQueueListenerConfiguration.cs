using Azure.Messaging.ServiceBus.Administration;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;

namespace Wolverine.AzureServiceBus;

public class AzureServiceBusQueueListenerConfiguration : ListenerConfiguration<AzureServiceBusQueueListenerConfiguration, AzureServiceBusQueue>
{
    public AzureServiceBusQueueListenerConfiguration(AzureServiceBusQueue queue) : base(queue)
    {
    }
    
    /// <summary>
    /// Add circuit breaker exception handling to this listener
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AzureServiceBusQueueListenerConfiguration CircuitBreaker(Action<CircuitBreakerOptions>? configure = null)
    {
        add(e =>
        {
            e.CircuitBreakerOptions = new CircuitBreakerOptions();
            configure?.Invoke(e.CircuitBreakerOptions);
        });

        return this;
    }

    /// <summary>
    /// Configure the underlying Azure Service Bus queue. This is only applicable when
    /// Wolverine is creating the queues
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AzureServiceBusQueueListenerConfiguration ConfigureQueue(Action<CreateQueueOptions> configure)
    {
        add(e => configure(e.Options));
        return this;
    }
}