using System.Net;
using System.Text.Json;
using DotPulsar;
using DotPulsar.Abstractions;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Pulsar.ErrorHandling;

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
        // doesn't apply the policy?!?:
        //endpoints.Policies.Add<PulsarNativeResiliencyPolicy>();
        //endpoints.Policies.Add(new PulsarNativeResiliencyPolicy());

        new PulsarNativeResiliencyPolicy().Apply(endpoints);

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

    /// <summary>
    ///     Set the specified unsubscribe on close setting for all Pulsar endpoints.
    /// </summary>
    /// <param name="options"></param>
    /// <param name="unsubscribeOnClose"></param>
    /// <returns></returns>
    public static IPolicies UnsubscribePulsarOnClose(this IPolicies policies, PulsarUnsubscribeOnClose unsubscribeOnClose)
    {
        policies.Add(new PulsarUnsubscribeOnClosePolicy(unsubscribeOnClose));
        return policies;
    }

    /// <summary>
    ///     Disable the possibility of requeueing messages for all Pulsar endpoints.
    /// </summary>
    /// <param name="options"></param>
    /// <returns></returns>
    public static IPolicies DisablePulsarRequeue(this IPolicies policies)
    {
        policies.Add(new PulsarEnableRequeuePolicy(PulsarRequeue.Disabled));
        return policies;
    }

    /// <summary>
    ///     Apply CloudEvents interop to all Pulsar endpoints. This configures both
    ///     listening and sending endpoints to use the CloudEvents message format.
    /// </summary>
    /// <param name="policies"></param>
    /// <param name="jsonSerializerOptions">Optional JSON serializer options for CloudEvents serialization</param>
    /// <returns></returns>
    public static IPolicies UsePulsarWithCloudEvents(this IPolicies policies, JsonSerializerOptions? jsonSerializerOptions = null)
    {
        policies.Add(new PulsarCloudEventsPolicy(jsonSerializerOptions));
        return policies;
    }
}

public class PulsarListenerConfiguration : InteroperableListenerConfiguration<PulsarListenerConfiguration, PulsarEndpoint, IPulsarEnvelopeMapper, PulsarEnvelopeMapper>
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

        // TODO: check how to restrict it properly
        //if (subscriptionType is DotPulsar.SubscriptionType.Shared or DotPulsar.SubscriptionType.KeyShared)
        //    return new PulsarSharedListenerConfiguration(this._endpoint);

        return this;
    }

    /// <summary>
    /// Override the Pulsar subscription type to  <see cref="DotPulsar.SubscriptionType.Failover"/> for just this topic
    /// </summary>
    /// <param name="subscriptionType"></param>
    /// <returns></returns>
    public PulsarListenerConfiguration WithFailoverSubscriptionType()
    {
        add(e => { e.SubscriptionType = DotPulsar.SubscriptionType.Failover; });

        return this;
    }

    /// <summary>
    /// Override the Pulsar subscription type to  <see cref="DotPulsar.SubscriptionType.Exclusive"/> for just this topic
    /// </summary>
    /// <param name="subscriptionType"></param>
    /// <returns></returns>
    public PulsarListenerConfiguration WithExclusiveSubscriptionType()
    {
        add(e => { e.SubscriptionType = DotPulsar.SubscriptionType.Exclusive; });

        return this;
    }

    /// <summary>
    /// Override the Pulsar subscription type to <see cref="DotPulsar.SubscriptionType.Shared"/> for just this topic
    /// </summary>
    /// <param name="subscriptionType"></param>
    /// <returns></returns>
    public PulsarNativeResiliencyDeadLetterConfiguration WithSharedSubscriptionType()
    {
        add(e => { e.SubscriptionType = DotPulsar.SubscriptionType.Shared; });

        return new PulsarNativeResiliencyDeadLetterConfiguration(new PulsarListenerConfiguration(_endpoint));
    }


    /// <summary>
    /// Override the Pulsar subscription type to <see cref="DotPulsar.SubscriptionType.KeyShared"/> for just this topic
    /// </summary>
    /// <param name="subscriptionType"></param>
    /// <returns></returns>
    public PulsarNativeResiliencyDeadLetterConfiguration WithKeySharedSubscriptionType()
    {
        add(e => { e.SubscriptionType = DotPulsar.SubscriptionType.KeyShared; });

        return new PulsarNativeResiliencyDeadLetterConfiguration(new PulsarListenerConfiguration(_endpoint));
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

    /// <summary>
    ///     Disable the possibility of requeueing messages
    /// </summary>
    /// <returns></returns>
    public PulsarListenerConfiguration DisableRequeue()
    {
        add(e =>
        {
            e.EnableRequeue = false;
        });

        return this;
    }
    
        /// <summary>
    /// Customize the dead letter queueing for this specific endpoint
    /// </summary>
    /// <param name="configure">Optional configuration</param>
    /// <returns></returns>
    public PulsarListenerConfiguration DeadLetterQueueing(DeadLetterTopic dlq)
    {
        add(e =>
        {
            e.DeadLetterTopic = dlq;
            e.Runtime.Options.Policies.OnAnyException().MoveToErrorQueue();
        });

        return this;
    }

    /// <summary>
    ///     Set whether the subscription should be unsubscribed when the listener is closed.
    /// </summary>
    /// <param name="unsubscribeOnClose"></param>
    /// <returns></returns>
    public PulsarListenerConfiguration UnsubscribeOnClose(bool unsubscribeOnClose)
    {
        add(e =>
        {
            e.UnsubscribeOnClose = unsubscribeOnClose;
        });

        return this;
    }
    
    internal void Apply(Action<PulsarEndpoint> action)
    {
        add(action);
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

public class PulsarNativeResiliencyConfig
{
    public DeadLetterTopic? DeadLetterTopic { get; set; }
    public RetryLetterTopic? RetryLetterTopic { get; set; }


    public Action<PulsarEndpoint> Apply()
    {
        return endpoint =>
        {
            if (RetryLetterTopic is null && DeadLetterTopic is null)
            {
                endpoint.DeadLetterTopic = null;
                endpoint.RetryLetterTopic = null;
                return;
            }

            // Set the DLQ configuration regardless
            if (DeadLetterTopic is not null)
            {
                endpoint.DeadLetterTopic = DeadLetterTopic;
            }

            if (RetryLetterTopic is not null)
            {
                // Validate subscription type
                if (endpoint.SubscriptionType is SubscriptionType.Failover or SubscriptionType.Exclusive)
                {
                    throw new InvalidOperationException(
                        "Pulsar does not support Retry letter queueing with Failover or Exclusive subscription types. Please use Shared or KeyShared subscription types.");
                }

                // Set retry configuration
                endpoint.RetryLetterTopic = RetryLetterTopic;

                if (endpoint.Runtime?.Options != null)
                {
                    endpoint.Runtime.Options.EnableAutomaticFailureAcks = false;
                }
            }
        };
    }

}

public abstract class PulsarNativeResiliencyConfiguration
{
    protected readonly PulsarListenerConfiguration Endpoint;
    protected PulsarNativeResiliencyConfig NativeResiliencyConfig;

    protected PulsarNativeResiliencyConfiguration(PulsarListenerConfiguration endpoint)
    {
        Endpoint = endpoint;
        NativeResiliencyConfig = new PulsarNativeResiliencyConfig();

    } 

    protected PulsarNativeResiliencyConfiguration(PulsarListenerConfiguration endpoint, PulsarNativeResiliencyConfig config)
    {
        Endpoint = endpoint;
        NativeResiliencyConfig = config;

    }

}


public class PulsarNativeResiliencyDeadLetterConfiguration : PulsarNativeResiliencyConfiguration
{


    public PulsarNativeResiliencyDeadLetterConfiguration(PulsarListenerConfiguration endpoint)
        : base(endpoint)
    {


    }

    /// <summary>
    /// Customize the dead letter queueing for this specific endpoint
    /// </summary>
    /// <param name="dlq">DLQ configuration</param>
    /// <returns></returns>
    public PulsarNativeResiliencyRetryLetterConfiguration DeadLetterQueueing(DeadLetterTopic dlq)
    {
        NativeResiliencyConfig.DeadLetterTopic = dlq;

        return new PulsarNativeResiliencyRetryLetterConfiguration(Endpoint, NativeResiliencyConfig);
    }

    /// <summary>
    /// Disable native DLQ functionality for this queue
    /// </summary>
    /// <returns></returns>
    public PulsarListenerConfiguration DisableDeadLetterQueueing()
    {
        return this.Endpoint;
    }
}

public class PulsarNativeResiliencyRetryLetterConfiguration : PulsarNativeResiliencyConfiguration
{

    public PulsarNativeResiliencyRetryLetterConfiguration(PulsarListenerConfiguration endpoint, PulsarNativeResiliencyConfig config)
        : base(endpoint, config)
    {


    }

    /// <summary>
    /// Customize the retry letter queueing for this specific endpoint
    /// </summary>
    /// <param name="configure">Optional configuration</param>
    /// <returns></returns>
    public PulsarListenerConfiguration RetryLetterQueueing(RetryLetterTopic rt)
    {
        NativeResiliencyConfig.RetryLetterTopic = rt;
        Endpoint.Apply(NativeResiliencyConfig.Apply());

        return Endpoint;
    }

    /// <summary>
    /// Disable native Retry letter functionality for this queue
    /// </summary>
    /// <returns></returns>
    public PulsarListenerConfiguration DisableRetryLetterQueueing()
    {
        NativeResiliencyConfig.RetryLetterTopic = null;
        Endpoint.Apply(NativeResiliencyConfig.Apply());

        return Endpoint;
    }
}

public class PulsarSubscriberConfiguration : InteroperableSubscriberConfiguration<PulsarSubscriberConfiguration, PulsarEndpoint, IPulsarEnvelopeMapper, PulsarEnvelopeMapper>
{
    public PulsarSubscriberConfiguration(PulsarEndpoint endpoint) : base(endpoint)
    {
    }
}
