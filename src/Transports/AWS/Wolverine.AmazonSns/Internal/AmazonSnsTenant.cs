using Amazon.SimpleNotificationService;
using JasperFx.Core;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Runtime;

namespace Wolverine.AmazonSns.Internal;

/// <summary>
/// Represents a single Wolverine tenant that is served by its own dedicated Amazon SNS connection (distinct AWS
/// account/credentials, region, or ServiceURL) while sharing the topic topology declared on the parent transport.
/// Because SNS is publish-only, a tenant needs only a tenant-specific publisher — no listener. Each tenant owns a
/// child <see cref="AmazonSnsTransport"/> whose SNS config is seeded from the parent and then re-pointed at the
/// tenant's own account/region, giving the tenant its own SNS client <em>and</em> its own topic (and TopicArn)
/// cache. It also carries a paired SQS client whose connection tracks the tenant's SNS account so that per-tenant
/// SQS subscription/queue-policy provisioning targets the same partition. Outbound routing is by
/// <see cref="Envelope.TenantId"/> via the framework's <see cref="Wolverine.Transports.Sending.TenantedSender"/>.
/// Mirrors the Amazon SQS tenant model (GH-3304).
/// </summary>
internal class AmazonSnsTenant
{
    public AmazonSnsTenant(string tenantId)
    {
        TenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
        Transport = new AmazonSnsTransport
        {
            // The child transport needs its own paired SQS transport for subscription provisioning.
            SQS = new AmazonSqsTransport()
        };
    }

    public string TenantId { get; }

    /// <summary>
    /// The child transport that provides this tenant's dedicated SNS client (and paired SQS client) plus its own
    /// topic/TopicArn cache. Its connection config is seeded from the parent's and re-pointed at the tenant's own
    /// account/region during <see cref="Compile"/>.
    /// </summary>
    public AmazonSnsTransport Transport { get; }

    /// <summary>
    /// Optional configuration hook applied to the tenant's own <see cref="AmazonSimpleNotificationServiceConfig"/>
    /// during <see cref="Compile"/>, <em>after</em> the parent's connection settings have been seeded onto it — so
    /// the tenant only overrides the axes it actually sets (typically region / ServiceURL / AuthenticationRegion).
    /// Runs at compile time rather than registration time because the parent connection is not fully configured
    /// until bootstrap completes. Null when the tenant is configured purely by supplying its own credentials.
    /// </summary>
    public Action<AmazonSimpleNotificationServiceConfig>? Configure { get; set; }

    /// <summary>
    /// Seed the tenant's child transport from the parent (credentials + connection endpoint + provisioning + queue
    /// policy), apply the tenant's own <see cref="Configure"/> overrides, align the paired SQS client to the tenant's
    /// final SNS account/region, then build the tenant's SNS and SQS clients. Called from
    /// <see cref="AmazonSnsTransport.ConnectAsync"/> once the parent connection has been fully resolved. The
    /// seed-then-override order (mirroring the SQS tenant model) is what lets a tenant re-point a single axis — e.g.
    /// just its region — while inheriting everything else from the parent. Note that
    /// <see cref="AmazonSimpleNotificationServiceConfig.RegionEndpoint"/> lazily resolves to an ambient default, so
    /// inheritance cannot rely on a null check for that axis.
    /// </summary>
    public AmazonSnsTransport Compile(AmazonSnsTransport parent, IWolverineRuntime runtime)
    {
        // Inherit the parent credential source unless the tenant supplied its own (dedicated-account case).
        Transport.CredentialSource ??= parent.CredentialSource;

        // Seed the parent's SNS connection endpoint. ServiceURL and RegionEndpoint are mutually exclusive on the
        // config (setting one clears the other), so copy whichever the parent is actually using; AuthenticationRegion
        // is independent (it lets a shared endpoint like LocalStack sign for a distinct region).
        if (parent.SnsConfig.ServiceURL.IsNotEmpty())
        {
            Transport.SnsConfig.ServiceURL = parent.SnsConfig.ServiceURL;
        }
        else if (parent.SnsConfig.RegionEndpoint != null)
        {
            Transport.SnsConfig.RegionEndpoint = parent.SnsConfig.RegionEndpoint;
        }

        Transport.SnsConfig.AuthenticationRegion = parent.SnsConfig.AuthenticationRegion;

        // The tenant's own overrides win over the seeded parent settings.
        Configure?.Invoke(Transport.SnsConfig);

        // Align the paired SQS client with the tenant's FINAL SNS connection so subscription + queue-policy
        // provisioning targets the same account/region/partition as the tenant's topic.
        if (Transport.SnsConfig.ServiceURL.IsNotEmpty())
        {
            Transport.SQS.Config.ServiceURL = Transport.SnsConfig.ServiceURL;
        }
        else if (Transport.SnsConfig.RegionEndpoint != null)
        {
            Transport.SQS.Config.RegionEndpoint = Transport.SnsConfig.RegionEndpoint;
        }

        Transport.SQS.Config.AuthenticationRegion = Transport.SnsConfig.AuthenticationRegion;

        // Provisioning + queue policy must match the parent so tenant topics/subscriptions are created the same way.
        Transport.AutoProvision = parent.AutoProvision;
        Transport.QueuePolicyBuilder = parent.QueuePolicyBuilder;

        Transport.SnsClient ??= Transport.BuildSnsClient(runtime);
        Transport.SqsClient ??= Transport.BuildSqsClient(runtime);

        return Transport;
    }
}
