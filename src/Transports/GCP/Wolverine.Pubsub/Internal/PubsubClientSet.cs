using Google.Api.Gax;
using Google.Cloud.PubSub.V1;

namespace Wolverine.Pubsub.Internal;

/// <summary>
/// A resolved set of Google Cloud Platform Pub/Sub API clients (plus the project id and emulator/streaming
/// configuration they were built with) for a single connection target. The default transport connection and each
/// per-tenant connection (broker-per-tenant, GH-3306) produce one of these; senders and listeners are parameterized
/// with a <see cref="PubsubClientSet"/> so the same endpoint topology can be published/consumed over a different
/// GCP project at runtime, keyed by <see cref="Envelope.TenantId"/>.
/// </summary>
public class PubsubClientSet
{
    public required string ProjectId { get; init; }
    public required EmulatorDetection EmulatorDetection { get; init; }
    public PublisherServiceApiClient? PublisherApiClient { get; init; }
    public SubscriberServiceApiClient? SubscriberApiClient { get; init; }

    /// <summary>
    /// Optional async hook applied to the streaming <see cref="SubscriberClientBuilder" /> each listener builds
    /// for this connection (e.g. per-tenant credentials).
    /// </summary>
    public Func<SubscriberClientBuilder, ValueTask>? ConfigureSubscriberClientBuilder { get; init; }
}
