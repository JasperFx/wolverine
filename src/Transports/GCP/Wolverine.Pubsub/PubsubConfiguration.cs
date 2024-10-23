using Google.Api.Gax;
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
    /// Enable WOlverine to create system endpoints automatically for responses and retries. This
    /// should probably be set if the application does have permissions to create topcis and subscriptions
    /// </summary>
    /// <returns></returns>
    public PubsubConfiguration EnableSystemEndpoints() {
        Transport.SystemEndpointsEnabled = true;

        return this;
    }

    /// <summary>
    /// Enable dead lettering with Google Cloud Pub/Sub within this entire
    /// application
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public PubsubConfiguration EnableDeadLettering(Action<PubsubDeadLetterOptions>? configure = null) {
        Transport.DeadLetter.Enabled = true;

        configure?.Invoke(Transport.DeadLetter);

        return this;
    }

    protected override PubsubTopicListenerConfiguration createListenerExpression(PubsubEndpoint listenerEndpoint) => new(listenerEndpoint);
    protected override PubsubTopicSubscriberConfiguration createSubscriberExpression(PubsubEndpoint subscriberEndpoint) => new(subscriberEndpoint);
}
