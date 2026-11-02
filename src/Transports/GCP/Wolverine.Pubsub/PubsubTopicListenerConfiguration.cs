using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Pubsub.Internal;
using Wolverine.Transports;

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
    ///     Opt into a separate Google Cloud Platform Pub/Sub subscription for every node, named
    ///     "{subscription}.{node}". Pub/Sub delivers a copy of every message to every subscription, so
    ///     <b>every node receives and handles every message</b>. Only use this for deliberate per-node
    ///     broadcast, like cache invalidation. By default, all nodes share one subscription and each
    ///     message is handled once across the cluster. Ignored for leader-pinned or exclusive listeners.
    /// </summary>
    /// <returns></returns>
    public PubsubTopicListenerConfiguration SubscriptionPerNode()
    {
        add(e => e.IsSubscriptionPerNode = true);

        return this;
    }

    /// <summary>
    ///     Mark the topic this listener reads from as owned by another system, typically the service that
    ///     publishes to it. Wolverine will not create or delete the topic, even when <c>AutoProvision()</c> is on,
    ///     but still creates this listener's subscription on it. If the topic does not exist yet, provisioning the
    ///     subscription fails with an <see cref="ExternallyOwnedObjectNotFoundException" /> that names it. GH-5342.
    /// </summary>
    /// <returns></returns>
    public PubsubTopicListenerConfiguration TopicIsExternallyOwned()
    {
        add(e => e.IsTopicExternallyOwned = true);

        return this;
    }

    /// <summary>
    ///     Mark this endpoint's topic and subscription as owned by another system. Wolverine will not create or
    ///     delete either of them, even when <c>AutoProvision()</c> is on, and only listens to the subscription.
    ///     <c>resources check</c> still checks that they exist. Use <see cref="TopicIsExternallyOwned" /> instead
    ///     when Wolverine should still create the subscription. GH-5342.
    /// </summary>
    /// <returns></returns>
    public PubsubTopicListenerConfiguration ExternallyOwned()
    {
        add(e => e.IsExternallyOwned = true);

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