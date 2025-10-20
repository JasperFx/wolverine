using Google.Api.Gax;
using Wolverine.Pubsub.Internal;
using Wolverine.Transports;

namespace Wolverine.Pubsub;

public class PubsubConfiguration : BrokerExpression<
    PubsubTransport,
    PubsubSubscription,
    PubsubTopic,
    PubsubTopicListenerConfiguration,
    PubsubTopicSubscriberConfiguration,
    PubsubConfiguration
>
{
    public PubsubConfiguration(PubsubTransport transport, WolverineOptions options) : base(transport, options)
    {
    }

    /// <summary>
    ///     Set emulator detection for the Google Cloud Platform Pub/Sub transport
    /// </summary>
    /// <remarks>
    ///     Remember to set the environment variable `PUBSUB_EMULATOR_HOST` to the emulator's host and port
    ///     and the eniviroment variable `PUBSUB_PROJECT_ID` to a project id
    /// </remarks>
    /// <param name="configure"></param>
    /// <returns></returns>
    public PubsubConfiguration UseEmulatorDetection(
        EmulatorDetection emulatorDetection = EmulatorDetection.EmulatorOrProduction)
    {
        Transport.EmulatorDetection = emulatorDetection;

        return this;
    }

    /// <summary>
    ///     Opt into using conventional message routing using
    ///     queues based on message type names
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public PubsubConfiguration UseConventionalRouting(
        Action<PubsubMessageRoutingConvention>? configure = null
    )
    {
        var routing = new PubsubMessageRoutingConvention();

        configure?.Invoke(routing);

        Options.RouteWith(routing);

        return this;
    }

    protected override PubsubTopicListenerConfiguration createListenerExpression(PubsubSubscription listenerEndpoint)
    {
        return new PubsubTopicListenerConfiguration(listenerEndpoint);
    }

    protected override PubsubTopicSubscriberConfiguration createSubscriberExpression(PubsubTopic subscriberEndpoint)
    {
        return new PubsubTopicSubscriberConfiguration(subscriberEndpoint);
    }

    /// <summary>
    /// Enable Wolverine to create a topic & subscription for the node to be used for request/reply mechanics
    /// and for inter-node communication for leader election and agent assignements
    /// </summary>
    /// <returns></returns>
    public PubsubConfiguration EnableSystemEndpoints()
    {
        Transport.SystemEndpointsEnabled = true;
        return this;
    }
}