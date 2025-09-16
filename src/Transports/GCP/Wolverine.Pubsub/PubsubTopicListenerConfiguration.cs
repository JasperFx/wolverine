using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Pubsub.Internal;

namespace Wolverine.Pubsub;

public class PubsubTopicListenerConfiguration : InteroperableListenerConfiguration<PubsubTopicListenerConfiguration, PubsubEndpoint, IPubsubEnvelopeMapper, PubsubEnvelopeMapper>
{
    public PubsubTopicListenerConfiguration(PubsubEndpoint endpoint) : base(endpoint)
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
    ///     Configure the underlying Google Cloud Platform Pub/Sub topic. This is only applicable when
    ///     Wolverine is creating the topic.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public PubsubTopicListenerConfiguration ConfigurePubsubTopic(Action<CreateTopicOptions> configure)
    {
        add(e => configure(e.Server.Topic.Options));

        return this;
    }

    /// <summary>
    ///     Configure the underlying Google Cloud Platform Pub/Sub subscription. This is only applicable when
    ///     Wolverine is creating the subscription.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public PubsubTopicListenerConfiguration ConfigurePubsubSubscription(Action<CreateSubscriptionOptions> configure)
    {
        add(e => configure(e.Server.Subscription.Options));

        return this;
    }

    /// <summary>
    ///     Configure the underlying Google Cloud Platform Pub/Sub subscriber.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public PubsubTopicListenerConfiguration ConfigureListener(Action<PubsubClientOptions> configure)
    {
        add(e => configure(e.Client));

        return this;
    }

    /// <summary>
    ///     Completely disable all Google Cloud Platform Pub/Sub dead lettering for just this endpoint
    /// </summary>
    /// <returns></returns>
    public PubsubTopicListenerConfiguration DisableDeadLettering()
    {
        add(e =>
        {
            e.DeadLetterName = null;
            e.Server.Subscription.Options.DeadLetterPolicy = null;
        });

        return this;
    }

    /// <summary>
    ///     Customize the dead lettering for just this endpoint
    /// </summary>
    /// <param name="deadLetterName"></param>
    /// <param name="configure">Optionally configure properties of the dead letter itself</param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public PubsubTopicListenerConfiguration ConfigureDeadLettering(
        string deadLetterName,
        Action<PubsubEndpoint>? configure = null
    )
    {
        add(e =>
        {
            e.DeadLetterName = deadLetterName;

            if (configure is not null)
            {
                e.ConfigureDeadLetter(configure);
            }
        });

        return this;
    }
}
