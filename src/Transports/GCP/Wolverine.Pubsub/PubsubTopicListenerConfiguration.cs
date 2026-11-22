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
    ///     GH-5822. Only for <c>ListenToPubsubSubscription()</c>: the topic the existing subscription is attached to,
    ///     which is where a requeue copy of a message received on it is published. Without this, Wolverine reads the
    ///     topic from the subscription at startup, which takes the <c>pubsub.subscriptions.get</c> permission. When
    ///     neither works, a requeue nacks the delivery (Inline) or requeues it in memory (buffered) instead.
    ///     The <see cref="PubsubTransport.IdentifierPrefix" /> is not applied to this name.
    /// </summary>
    /// <param name="topicName">
    ///     The topic id in this broker's project, or a full topic resource name
    ///     (<c>projects/{project}/topics/{topic}</c>) for a topic in another project
    /// </param>
    /// <returns></returns>
    public PubsubTopicListenerConfiguration FromTopic(string topicName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);

        add(e =>
        {
            if (!e.IsExistingSubscription)
            {
                throw new InvalidOperationException(
                    $"FromTopic() only applies to a listener on an existing subscription (ListenToPubsubSubscription()), but {e.Uri} listens to topic '{e.Server.Topic.Name.TopicId}'.");
            }

            e.ExplicitSubscriptionTopic = PubsubEndpoint.ParseTopicName(topicName, e.Transport.ProjectId);
            e.Server.Topic.Name = e.ExplicitSubscriptionTopic;
        });

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