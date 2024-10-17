using Wolverine.Pubsub.Internal;
using Wolverine.Transports;

namespace Wolverine.Pubsub;

public class PubsubConfiguration : BrokerExpression<
    PubsubTransport,
    PubsubSubscription,
    PubsubTopic,
    PubsubSubscriptionConfiguration,
    PubsubTopicConfiguration,
    PubsubConfiguration
> {
    public PubsubConfiguration(PubsubTransport transport, WolverineOptions options) : base(transport, options) { }

    /// <summary>
    /// Opt into using conventional message routing using topics and
    /// subscriptions based on message type names
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public PubsubConfiguration UseTopicAndSubscriptionConventionalRouting(
        Action<PubsubTopicBroadcastingRoutingConvention>? configure = null
    ) {
        var routing = new PubsubTopicBroadcastingRoutingConvention();

        configure?.Invoke(routing);

        Options.RouteWith(routing);

        return this;
    }

    /// <summary>
    /// Opt into using conventional message routing using
    /// queues based on message type names
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public PubsubConfiguration UseConventionalRouting(
        Action<PubsubMessageRoutingConvention>? configure = null
    ) {
        var routing = new PubsubMessageRoutingConvention();

        configure?.Invoke(routing);

        Options.RouteWith(routing);

        return this;
    }

    /// <summary>
    /// Is Wolverine enabled to create system endpoints automatically for responses and retries? This
    /// should probably be set to false if the application does not have permissions to create topcis and subscriptions
    /// </summary>
    /// <param name="enabled"></param>
    /// <returns></returns>
    public PubsubConfiguration SystemEndpointsAreEnabled(bool enabled) {
        Transport.SystemEndpointsEnabled = enabled;

        return this;
    }

    /// <summary>
    /// Globally enable all native dead lettering with Google Cloud Pub/Sub within this entire
    /// application
    /// </summary>
    /// <returns></returns>
    public PubsubConfiguration EnableAllNativeDeadLettering() {
        Transport.EnableDeadLettering = true;

        return this;
    }

    protected override PubsubSubscriptionConfiguration createListenerExpression(PubsubSubscription subscriberEndpoint) => new(subscriberEndpoint);
    protected override PubsubTopicConfiguration createSubscriberExpression(PubsubTopic topicEndpoint) => new(topicEndpoint);
}
