using Google.Api.Gax;
using Wolverine.Pubsub.Internal;
using Wolverine.Transports;

namespace Wolverine.Pubsub;

public class PubsubConfiguration : BrokerExpression<
    PubsubTransport,
    PubsubEndpoint,
    PubsubEndpoint,
    PubsubTopicListenerConfiguration,
    PubsubTopicSubscriberConfiguration,
    PubsubConfiguration
> {
    public PubsubConfiguration(PubsubTransport transport, WolverineOptions options) : base(transport, options) { }

    /// <summary>
    ///     Set emulator detection for the Google Cloud Pub/Sub transport
    /// </summary>
    /// <remarks>
    ///     Remember to set the environment variable `PUBSUB_EMULATOR_HOST` to the emulator's host and port
    ///     and the eniviroment variable `PUBSUB_PROJECT_ID` to a project id
    /// </remarks>
    /// <param name="configure"></param>
    /// <returns></returns>
    public PubsubConfiguration UseEmulatorDetection(EmulatorDetection emulatorDetection = EmulatorDetection.EmulatorOrProduction) {
        Transport.EmulatorDetection = emulatorDetection;

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

    protected override PubsubTopicListenerConfiguration createListenerExpression(PubsubEndpoint listenerEndpoint) => new(listenerEndpoint);
    protected override PubsubTopicSubscriberConfiguration createSubscriberExpression(PubsubEndpoint subscriberEndpoint) => new(subscriberEndpoint);
}
