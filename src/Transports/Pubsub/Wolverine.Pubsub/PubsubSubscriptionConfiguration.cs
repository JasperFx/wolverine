using Wolverine.Pubsub.Internal;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;

namespace Wolverine.Pubsub;

public class PubsubSubscriptionConfiguration : ListenerConfiguration<PubsubSubscriptionConfiguration, PubsubSubscription> {
    public PubsubSubscriptionConfiguration(PubsubSubscription endpoint) : base(endpoint) { }

    /// <summary>
    ///     Add circuit breaker exception handling to this listener
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public PubsubSubscriptionConfiguration CircuitBreaker(Action<CircuitBreakerOptions>? configure = null) {
        add(e => {
            e.CircuitBreakerOptions = new CircuitBreakerOptions();

            configure?.Invoke(e.CircuitBreakerOptions);
        });

        return this;
    }

    /// <summary>
    ///     Configure the underlying Google Cloud Pub/Sub subscription. This is only applicable when
    ///     Wolverine is creating the subscriptions
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public PubsubSubscriptionConfiguration ConfigureSubscription(Action<PubsubSubscription> configure) {
        add(s => configure(s));

        return this;
    }

    /// <summary>
    /// Completely disable all Google Cloud Pub/Sub dead lettering for just this subscription
    /// </summary>
    /// <returns></returns>
    public PubsubSubscriptionConfiguration DisableDeadLettering() {
        add(e => e.DeadLetterName = null);

        return this;
    }

    /// <summary>
    /// Customize the dead lettering for just this subscription
    /// </summary>
    /// <param name="deadLetterName"></param>
    /// <param name="configure">Optionally configure properties of the dead lettering itself</param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public PubsubSubscriptionConfiguration ConfigureDeadLettering(
        string deadLetterName,
        Action<PubsubSubscription>? configure = null
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
    public PubsubSubscriptionConfiguration InteropWith(IPubsubEnvelopeMapper mapper) {
        add(e => e.Mapper = mapper);

        return this;
    }
}
