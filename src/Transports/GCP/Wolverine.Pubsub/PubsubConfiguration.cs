using Google.Api.Gax;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.PubSub.V1;
using Wolverine.Transports;

namespace Wolverine.Pubsub;

public class PubsubConfiguration : BrokerExpression<
    PubsubTransport,
    PubsubEndpoint,
    PubsubEndpoint,
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

    /// <summary>
    ///     Opt into using conventional message routing with the specified naming source.
    ///     Using <see cref="NamingSource.FromHandlerType"/> is appropriate for modular monolith
    ///     scenarios where you have more than one handler for a given message type.
    /// </summary>
    /// <param name="namingSource"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public PubsubConfiguration UseConventionalRouting(NamingSource namingSource,
        Action<PubsubMessageRoutingConvention>? configure = null
    )
    {
        var routing = new PubsubMessageRoutingConvention();
        routing.UseNaming(namingSource);

        configure?.Invoke(routing);

        Options.RouteWith(routing);

        return this;
    }

    /// <summary>
    ///     Enable Wolverine to create system endpoints automatically for responses and retries. This
    ///     should probably be set if the application does have permissions to create topcis and subscriptions
    /// </summary>
    /// <returns></returns>
    public PubsubConfiguration EnableSystemEndpoints()
    {
        Transport.SystemEndpointsEnabled = true;

        return this;
    }

    /// <summary>
    ///     Enable dead lettering with Google Cloud Platform Pub/Sub within this entire
    ///     application
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public PubsubConfiguration EnableDeadLettering(Action<PubsubDeadLetterOptions>? configure = null)
    {
        Transport.DeadLetter.Enabled = true;

        configure?.Invoke(Transport.DeadLetter);

        return this;
    }

    /// <summary>
    ///     Configure the <see cref="PublisherServiceApiClientBuilder" /> used to create the publisher API client.
    ///     Called after <see cref="EmulatorDetection" /> is applied, so this callback may override transport-level
    ///     defaults. Multiple calls compose in order.
    /// </summary>
    public PubsubConfiguration ConfigurePublisherApiClient(Action<PublisherServiceApiClientBuilder> configure)
    {
        var existing = Transport.ConfigurePublisherApiBuilder;
        Transport.ConfigurePublisherApiBuilder = existing == null
            ? configure
            : b => { existing(b); configure(b); };
        return this;
    }

    /// <summary>
    ///     Configure the <see cref="SubscriberServiceApiClientBuilder" /> used to create the subscriber API client.
    ///     Called after <see cref="EmulatorDetection" /> is applied, so this callback may override transport-level
    ///     defaults. Multiple calls compose in order.
    /// </summary>
    public PubsubConfiguration ConfigureSubscriberApiClient(Action<SubscriberServiceApiClientBuilder> configure)
    {
        var existing = Transport.ConfigureSubscriberApiBuilder;
        Transport.ConfigureSubscriberApiBuilder = existing == null
            ? configure
            : b => { existing(b); configure(b); };
        return this;
    }

    /// <summary>
    ///     Configure the <see cref="SubscriberClientBuilder" /> used to create subscriber clients for each listener.
    ///     Called after <see cref="EmulatorDetection" /> is applied, so this callback may override transport-level
    ///     defaults. Multiple calls compose in order.
    /// </summary>
    public PubsubConfiguration ConfigureSubscriberClient(Action<SubscriberClientBuilder> configure)
    {
        var existing = Transport.ConfigureSubscriberClientBuilder;
        Transport.ConfigureSubscriberClientBuilder = existing == null
            ? configure
            : b => { existing(b); configure(b); };
        return this;
    }

    /// <summary>
    ///     Provide a <see cref="GoogleCredential" /> for authenticating with Google Cloud Platform Pub/Sub.
    ///     The credential manages its own token refresh lifecycle, including Workload Identity Federation scenarios.
    ///     This is a convenience shorthand for calling <see cref="ConfigurePublisherApiClient" />,
    ///     <see cref="ConfigureSubscriberApiClient" />, and <see cref="ConfigureSubscriberClient" /> individually.
    /// </summary>
    public PubsubConfiguration UseCredential(GoogleCredential credential)
    {
        ConfigurePublisherApiClient(b => b.GoogleCredential = credential);
        ConfigureSubscriberApiClient(b => b.GoogleCredential = credential);
        ConfigureSubscriberClient(b => b.GoogleCredential = credential);
        return this;
    }

    protected override PubsubTopicListenerConfiguration createListenerExpression(PubsubEndpoint listenerEndpoint)
    {
        return new PubsubTopicListenerConfiguration(listenerEndpoint);
    }

    protected override PubsubTopicSubscriberConfiguration createSubscriberExpression(PubsubEndpoint subscriberEndpoint)
    {
        return new PubsubTopicSubscriberConfiguration(subscriberEndpoint);
    }
}