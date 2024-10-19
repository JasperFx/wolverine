using Wolverine.Pubsub.Internal;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;

namespace Wolverine.Pubsub;

public class PubsubTopicListenerConfiguration : ListenerConfiguration<PubsubTopicListenerConfiguration, PubsubEndpoint> {
    public PubsubTopicListenerConfiguration(PubsubEndpoint endpoint) : base(endpoint) { }

    /// <summary>
    ///     Add circuit breaker exception handling to this listener
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public PubsubTopicListenerConfiguration CircuitBreaker(Action<CircuitBreakerOptions>? configure = null) {
        add(e => {
            e.CircuitBreakerOptions = new CircuitBreakerOptions();

            configure?.Invoke(e.CircuitBreakerOptions);
        });

        return this;
    }

    /// <summary>
    ///     Configure the underlying Google Cloud Pub/Sub topic and subscription. This is only applicable when
    ///     Wolverine is creating the topic and subscription
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public PubsubTopicListenerConfiguration ConfigureServer(Action<PubsubServerOptions> configure) {
        add(e => configure(e.Server));

        return this;
    }

    /// <summary>
    ///     Configure the underlying Google Cloud Pub/Sub subscriber client. This is only applicable when
    ///     Wolverine is creating the subscriber client
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public PubsubTopicListenerConfiguration ConfigureClient(Action<PubsubClientOptions> configure) {
        add(e => configure(e.Client));

        return this;
    }

    /// <summary>
    /// Completely disable all Google Cloud Pub/Sub dead lettering for just this endpoint
    /// </summary>
    /// <returns></returns>
    public PubsubTopicListenerConfiguration DisableDeadLettering() {
        add(e => {
            e.DeadLetterName = null;
            e.Server.Subscription.Options.DeadLetterPolicy = null;
        });

        return this;
    }

    /// <summary>
    /// Customize the dead lettering for just this endpoint
    /// </summary>
    /// <param name="deadLetterName"></param>
    /// <param name="configure">Optionally configure properties of the dead lettering itself</param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public PubsubTopicListenerConfiguration ConfigureDeadLettering(
        string deadLetterName,
        Action<PubsubEndpoint>? configure = null
    ) {
        add(e => {
            e.DeadLetterName = deadLetterName;

            if (configure is not null) e.ConfigureDeadLetter(configure);
        });

        return this;
    }

    /// <summary>
    /// Utilize custom envelope mapping for Google Cloud Pub/Sub interoperability with external non-Wolverine systems
    /// </summary>
    /// <param name="mapper"></param>
    /// <returns></returns>
    public PubsubTopicListenerConfiguration InteropWith(IPubsubEnvelopeMapper mapper) {
        add(e => e.Mapper = mapper);

        return this;
    }
}
