using Google.Cloud.PubSub.V1;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Pubsub.Internal;

namespace Wolverine.Pubsub;

public class PubsubTopicListenerConfiguration : InteroperableListenerConfiguration<PubsubTopicListenerConfiguration, PubsubSubscription, IPubsubEnvelopeMapper, PubsubEnvelopeMapper>
{
    public PubsubTopicListenerConfiguration(PubsubSubscription endpoint) : base(endpoint)
    {
    }

    /// <summary>
    ///     Add circuit breaker exception handling to this listener
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public PubsubTopicListenerConfiguration CircuitBreaker(Action<CircuitBreakerOptions>? configure = null)
    {
        add(e =>
        {
            e.CircuitBreakerOptions = new CircuitBreakerOptions();

            configure?.Invoke(e.CircuitBreakerOptions);
        });

        return this;
    }

    /// <summary>
    ///     Configure the underlying Google Cloud Platform Pub/Sub subscription. This is only applicable when
    ///     Wolverine is creating the subscription.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public PubsubTopicListenerConfiguration ConfigurePubsubSubscription(Action<Subscription> configure)
    {
        add(e => configure(e.Options));

        return this;
    }
}