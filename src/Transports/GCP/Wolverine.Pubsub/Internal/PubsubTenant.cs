using Google.Api.Gax;
using Google.Cloud.PubSub.V1;

namespace Wolverine.Pubsub.Internal;

/// <summary>
/// A single tenant in the broker-per-tenant Google Cloud Platform Pub/Sub model (GH-3306). Project-id-per-tenant is
/// the natural isolation axis: <see cref="TopicName" />/<see cref="SubscriptionName" /> embed the project id, so a
/// tenant pointed at its own <see cref="ProjectId" /> already yields physically distinct GCP resources for the same
/// logical topology. Each tenant owns its own <see cref="PublisherServiceApiClient" />/<see cref="SubscriberServiceApiClient" />
/// pair (built in <see cref="ConnectAsync" />), and optionally its own credential hooks (seeded from the parent
/// transport). Modeled after the NATS per-tenant connection support.
/// </summary>
public class PubsubTenant
{
    public PubsubTenant(string tenantId, string projectId)
    {
        TenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
        ProjectId = projectId ?? throw new ArgumentNullException(nameof(projectId));
    }

    public string TenantId { get; }

    /// <summary>
    /// The GCP project id that isolates this tenant's topology. Distinct from the parent/default project id.
    /// </summary>
    public string ProjectId { get; }

    /// <summary>
    /// Emulator detection for this tenant's clients. Seeded from the parent transport so tenant tests run against
    /// the same emulator; may be overridden per tenant.
    /// </summary>
    public EmulatorDetection EmulatorDetection { get; set; } = EmulatorDetection.None;

    /// <summary>
    /// Optional async hook to configure this tenant's <see cref="PublisherServiceApiClientBuilder" /> (e.g. dedicated
    /// credentials). Seeded from the parent transport unless overridden.
    /// </summary>
    public Func<PublisherServiceApiClientBuilder, ValueTask>? ConfigurePublisherApiBuilder { get; set; }

    /// <summary>
    /// Optional async hook to configure this tenant's <see cref="SubscriberServiceApiClientBuilder" />. Seeded from
    /// the parent transport unless overridden.
    /// </summary>
    public Func<SubscriberServiceApiClientBuilder, ValueTask>? ConfigureSubscriberApiBuilder { get; set; }

    /// <summary>
    /// Optional async hook to configure the streaming <see cref="SubscriberClientBuilder" /> each of this tenant's
    /// listeners builds. Seeded from the parent transport unless overridden.
    /// </summary>
    public Func<SubscriberClientBuilder, ValueTask>? ConfigureSubscriberClientBuilder { get; set; }

    /// <summary>
    /// The resolved client set for this tenant, built once during the transport's ConnectAsync and owned for the
    /// lifetime of the transport.
    /// </summary>
    internal PubsubClientSet? Clients { get; private set; }

    internal async Task ConnectAsync()
    {
        var pubBuilder = new PublisherServiceApiClientBuilder
        {
            EmulatorDetection = EmulatorDetection
        };
        if (ConfigurePublisherApiBuilder != null)
        {
            await ConfigurePublisherApiBuilder(pubBuilder);
        }

        var subBuilder = new SubscriberServiceApiClientBuilder
        {
            EmulatorDetection = EmulatorDetection
        };
        if (ConfigureSubscriberApiBuilder != null)
        {
            await ConfigureSubscriberApiBuilder(subBuilder);
        }

        Clients = new PubsubClientSet
        {
            ProjectId = ProjectId,
            EmulatorDetection = EmulatorDetection,
            PublisherApiClient = await pubBuilder.BuildAsync(),
            SubscriberApiClient = await subBuilder.BuildAsync(),
            ConfigureSubscriberClientBuilder = ConfigureSubscriberClientBuilder
        };
    }
}
