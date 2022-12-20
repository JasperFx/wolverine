using System;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;

namespace Wolverine.Transports.Local;

public class LocalQueueConfiguration : ListenerConfiguration<LocalQueueConfiguration, LocalQueue>
{
    public LocalQueueConfiguration(LocalQueue endpoint) : base(endpoint)
    {
    }

    /// <summary>
    /// Limit all outgoing messages to a certain "deliver within" time span after which the messages
    /// will be discarded even if not successfully delivered or processed
    /// </summary>
    /// <param name="timeToLive"></param>
    /// <returns></returns>
    public LocalQueueConfiguration DeliverWithin(TimeSpan timeToLive)
    {
        add(e => e.OutgoingRules.Add(new DeliverWithinRule(timeToLive)));
        return this;
    }

    /// <summary>
    ///     Add circuit breaker exception handling to this local queue. This will only
    ///     be applied if the local queue is marked as durable!!!
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public LocalQueueConfiguration CircuitBreaker(Action<CircuitBreakerOptions>? configure = null)
    {
        add(e =>
        {
            e.CircuitBreakerOptions = new CircuitBreakerOptions();
            configure?.Invoke(e.CircuitBreakerOptions);
        });

        return this;
    }
}