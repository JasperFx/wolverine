using Amazon.SQS;
using JasperFx.Core;
using Wolverine.Runtime;

namespace Wolverine.AmazonSqs.Internal;

/// <summary>
/// Represents a single Wolverine tenant that is served by its own dedicated Amazon SQS connection (distinct AWS
/// account/credentials, region, or ServiceURL) while sharing the queue topology declared on the parent transport.
/// Because an SQS "connection" is credential + region config plus a lazily built <see cref="Amazon.SQS.IAmazonSQS"/>
/// client, and because <see cref="AmazonSqsQueue.QueueUrl"/> is cached per endpoint (and would collide across
/// tenants), each tenant owns a child <see cref="AmazonSqsTransport"/> whose config is seeded from the parent and
/// then re-pointed at the tenant's own account/region — giving the tenant its own client <em>and</em> its own queue
/// (and QueueUrl) cache. Outbound routing is by <see cref="Envelope.TenantId"/> via the framework's
/// <see cref="Wolverine.Transports.Sending.TenantedSender"/>; inbound the tenant's own listener stamps the tenant id.
/// Mirrors the Azure Service Bus tenant model.
/// </summary>
internal class AmazonSqsTenant
{
    public AmazonSqsTenant(string tenantId)
    {
        TenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
        Transport = new AmazonSqsTransport();
    }

    public string TenantId { get; }

    /// <summary>
    /// The child transport that provides this tenant's dedicated SQS client and its own queue/QueueUrl cache. Its
    /// connection config is seeded from the parent's and re-pointed at the tenant's own account/region during
    /// <see cref="Compile"/>.
    /// </summary>
    public AmazonSqsTransport Transport { get; }

    /// <summary>
    /// Optional configuration hook applied to the tenant's own <see cref="AmazonSQSConfig"/> during
    /// <see cref="Compile"/>, <em>after</em> the parent's connection settings have been seeded onto it — so the
    /// tenant only overrides the axes it actually sets (typically region / ServiceURL / AuthenticationRegion). Runs
    /// at compile time rather than registration time because the parent connection is not fully configured until
    /// bootstrap completes. Null when the tenant is configured purely by supplying its own credentials.
    /// </summary>
    public Action<AmazonSQSConfig>? Configure { get; set; }

    /// <summary>
    /// Seed the tenant's child transport from the parent (credentials + connection endpoint + provisioning / DLQ
    /// behavior), apply the tenant's own <see cref="Configure"/> overrides, then build the tenant's SQS client.
    /// Called from <see cref="AmazonSqsTransport.ConnectAsync"/> once the parent connection has been fully resolved.
    /// The seed-then-override order (mirroring the Kafka tenant model) is what lets a tenant re-point a single axis —
    /// e.g. just its region — while inheriting everything else from the parent. Note that
    /// <see cref="AmazonSQSConfig.RegionEndpoint"/> is never truly null (it lazily resolves to an ambient default),
    /// so inheritance cannot rely on a null check — it seeds unconditionally and lets the tenant action win.
    /// </summary>
    public AmazonSqsTransport Compile(AmazonSqsTransport parent, IWolverineRuntime runtime)
    {
        // Inherit the parent credential source unless the tenant supplied its own (dedicated-account case).
        Transport.CredentialSource ??= parent.CredentialSource;

        // Seed the parent's connection endpoint. ServiceURL and RegionEndpoint are mutually exclusive on
        // AmazonSQSConfig (setting one clears the other), so copy whichever the parent is actually using;
        // AuthenticationRegion is independent (it lets a shared endpoint like LocalStack sign for a distinct region).
        if (parent.Config.ServiceURL.IsNotEmpty())
        {
            Transport.Config.ServiceURL = parent.Config.ServiceURL;
        }
        else if (parent.Config.RegionEndpoint != null)
        {
            Transport.Config.RegionEndpoint = parent.Config.RegionEndpoint;
        }

        Transport.Config.AuthenticationRegion = parent.Config.AuthenticationRegion;

        // The tenant's own overrides win over the seeded parent settings.
        Configure?.Invoke(Transport.Config);

        // Provision + dead-letter behavior must match the parent so tenant queues are created and dead-lettered the
        // same way on the tenant's own account.
        Transport.AutoProvision = parent.AutoProvision;
        Transport.AutoPurgeAllQueues = parent.AutoPurgeAllQueues;
        Transport.DisableDeadLetterQueues = parent.DisableDeadLetterQueues;
        Transport.DefaultDeadLetterQueueName = parent.DefaultDeadLetterQueueName;

        Transport.Client ??= Transport.BuildClient(runtime);

        return Transport;
    }
}
