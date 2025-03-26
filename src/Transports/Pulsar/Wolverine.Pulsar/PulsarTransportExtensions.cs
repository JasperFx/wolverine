using DotPulsar;
using DotPulsar.Abstractions;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;

namespace Wolverine.Pulsar;

public static class PulsarTransportExtensions
{
    /// <summary>
    ///     Quick access to the Pulsar Transport within this application.
    ///     This is for advanced usage
    /// </summary>
    /// <param name="endpoints"></param>
    /// <returns></returns>
    internal static PulsarTransport PulsarTransport(this WolverineOptions endpoints)
    {
        var transports = endpoints.As<WolverineOptions>().Transports;

        return transports.GetOrCreate<PulsarTransport>();
    }

    /// <summary>
    ///     Configure connection and authentication information about the Pulsar usage
    ///     within this Wolverine application
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="configure"></param>
    public static void UsePulsar(this WolverineOptions endpoints, Action<IPulsarClientBuilder> configure)
    {
        configure(endpoints.PulsarTransport().Builder);
    }

    /// <summary>
    ///     Connect to a local, standalone Pulsar broker at the default port
    /// </summary>
    /// <param name="endpoints"></param>
    public static void UsePulsar(this WolverineOptions endpoints)
    {
        endpoints.UsePulsar(_ => { });
    }

    /// <summary>
    ///     Publish matching messages to Pulsar using the named routing key or queue name and
    ///     optionally an exchange
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="topicPath">Pulsar topic of the form "persistent|non-persistent://tenant/namespace/topic"</param>
    /// <returns></returns>
    public static PulsarSubscriberConfiguration ToPulsarTopic(this IPublishToExpression publishing, string topicPath)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<PulsarTransport>();
        var endpoint = transport.EndpointFor(topicPath);

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(endpoint.Uri);

        return new PulsarSubscriberConfiguration(endpoint);
    }

    /// <summary>
    ///     Listen to a specified Pulsar topic path of the path "persistent|non-persistent://tenant/namespace/topic"
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="topicPath"></param>
    /// <returns></returns>
    public static PulsarListenerConfiguration ListenToPulsarTopic(this WolverineOptions endpoints, string topicPath)
    {
        var uri = PulsarEndpoint.UriFor(topicPath);
        var endpoint = endpoints.PulsarTransport()[uri];
        endpoint.IsListener = true;
        return new PulsarListenerConfiguration(endpoint);
    }
}

public class PulsarListenerConfiguration : ListenerConfiguration<PulsarListenerConfiguration, PulsarEndpoint>
{
    public PulsarListenerConfiguration(PulsarEndpoint endpoint) : base(endpoint)
    {
    }

    /// <summary>
    /// Provide a subscription name to Pulsar for this topic
    /// </summary>
    /// <param name="subscriptionName"></param>
    /// <returns></returns>
    public PulsarListenerConfiguration SubscriptionName(string subscriptionName)
    {
        add(e =>
        {
            e.SubscriptionName = subscriptionName;
        });

        return this;
    }
    
    /// <summary>
    /// Override the Pulsar subscription type for just this topic
    /// </summary>
    /// <param name="subscriptionType"></param>
    /// <returns></returns>
    public PulsarListenerConfiguration SubscriptionType(SubscriptionType subscriptionType)
    {
        add(e =>
        {
            e.SubscriptionType = subscriptionType;
        });

        return this;
    }

    /// <summary>
    /// Override the Pulsar subscription type for just this topic
    /// </summary>
    /// <param name="subscriptionType"></param>
    /// <returns></returns>
    public PulsarSharedListenerConfiguration WithSharedSubscriptionType()
    {
        add(e => { e.SubscriptionType = DotPulsar.SubscriptionType.Shared; });

        return new PulsarSharedListenerConfiguration(this._endpoint);
    }


    /// <summary>
    /// Override the Pulsar subscription type for just this topic
    /// </summary>
    /// <param name="subscriptionType"></param>
    /// <returns></returns>
    public PulsarSharedListenerConfiguration WithKeySharedSubscriptionType()
    {
        add(e => { e.SubscriptionType = DotPulsar.SubscriptionType.KeyShared; });

        return new PulsarSharedListenerConfiguration(this._endpoint);
    }

    /// <summary>
    ///     Add circuit breaker exception handling to this listener
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public PulsarListenerConfiguration CircuitBreaker(Action<CircuitBreakerOptions>? configure = null)
    {
        add(e =>
        {
            e.CircuitBreakerOptions = new CircuitBreakerOptions();
            configure?.Invoke(e.CircuitBreakerOptions);
        });


        return this;
    }

    // /// <summary>
    // /// To optimize the message listener throughput,
    // /// start up multiple listening endpoints. This is
    // /// most necessary when using inline processing
    // /// </summary>
    // /// <param name="count"></param>
    // /// <returns></returns>
    // public PulsarListenerConfiguration ListenerCount(int count)
    // {
    //     if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count), "Must be greater than zero");
    //
    //     endpoint.ListenerCount = count;
    //     return this;
    // }
}



public class PulsarSharedListenerConfiguration : ListenerConfiguration<PulsarListenerConfiguration, PulsarEndpoint>
{
    public PulsarSharedListenerConfiguration(PulsarEndpoint endpoint) : base(endpoint)
    {
    }


    /// <summary>
    /// Customize the dead letter queueing for this specific endpoint
    /// </summary>
    /// <param name="configure">Optional configuration</param>
    /// <returns></returns>
    public PulsarSharedListenerConfiguration DeadLetterQueueing(DeadLetterTopic dlq)
    {
        add(e =>
        {
            e.DeadLetterTopic = dlq;
        });

        return this;
    }

    /// <summary>
    /// Remove all dead letter queueing declarations from this queue
    /// </summary>
    /// <returns></returns>
    public PulsarSharedListenerConfiguration DisableDeadLetterQueueing()
    {
        add(e =>
        {
            e.DeadLetterTopic = null;
        });

        return this;
    }

    /// <summary>
    /// Customize the Retry letter queueing for this specific endpoint
    /// </summary>
    /// <param name="configure">Optional configuration</param>
    /// <returns></returns>
    public PulsarSharedListenerConfiguration RetryLetterQueueing(RetryTopic rt)
    {
        add(e =>
        {
            e.RetryLetterTopic = rt;
        });

        return this;
    }

    /// <summary>
    /// Remove all Retry letter queueing declarations from this queue
    /// </summary>
    /// <returns></returns>
    public PulsarSharedListenerConfiguration DisableRetryLetterQueueing()
    {
        add(e =>
        {
            e.RetryLetterTopic = null;
        });

        return this;
    }

}

public class PulsarSubscriberConfiguration : SubscriberConfiguration<PulsarSubscriberConfiguration, PulsarEndpoint>
{
    public PulsarSubscriberConfiguration(PulsarEndpoint endpoint) : base(endpoint)
    {
    }
}