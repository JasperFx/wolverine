using DotPulsar.Abstractions;

namespace Wolverine.Pulsar.Internal;

/// <summary>
/// A single broker-per-tenant registration for the Pulsar transport (GH-3308). Each tenant owns its own
/// child <see cref="PulsarTransport"/> (and therefore its own independent <see cref="IPulsarClient"/>) pointed
/// at a dedicated Pulsar cluster: service URL + auth are supplied through the tenant's own
/// <see cref="IPulsarClientBuilder"/> in <c>AddTenant</c>. Outbound is routed to the tenant's cluster by
/// <see cref="Envelope.TenantId"/>; inbound listeners stamp that tenant id.
///
/// IMPORTANT — the two "tenants" are orthogonal: this Wolverine tenant id is a <em>routing / connection</em>
/// concept and selects a whole <em>cluster</em>. It is NOT the native Pulsar tenant segment in the
/// <c>persistent://{tenant}/{namespace}/{topic}</c> topic hierarchy (that stays whatever the shared topic
/// topology declares). Two Wolverine tenants can — and normally do — use the same native tenant/namespace on
/// different clusters.
/// </summary>
internal class PulsarTenant
{
    public PulsarTenant(string tenantId)
    {
        TenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
        Transport = new PulsarTransport();
    }

    public string TenantId { get; }

    /// <summary>
    /// The tenant's own transport. Its <see cref="PulsarTransport.Builder"/> is configured by the
    /// <c>AddTenant</c> action (fully specifying ServiceUrl + auth) and its <see cref="PulsarTransport.Client"/>
    /// is built during the parent transport's <see cref="PulsarTransport.InitializeAsync"/>.
    /// </summary>
    public PulsarTransport Transport { get; }

    /// <summary>
    /// The user-supplied configuration for this tenant's DotPulsar client. Runs against a fresh
    /// <c>PulsarClient.Builder()</c> (DotPulsar's builder is not trivially cloneable), so the action must fully
    /// specify ServiceUrl and any authentication for the tenant's cluster.
    /// </summary>
    public Action<IPulsarClientBuilder> Configure { get; init; } = _ => { };

    /// <summary>
    /// Copy the parent transport's cross-cutting defaults (dead-letter / retry-letter topics) onto this
    /// tenant's transport and apply the tenant's own client-builder configuration. Called during the parent
    /// <see cref="PulsarTransport.InitializeAsync"/> before the tenant client is built. Native retry/DLQ
    /// producers are provisioned per-tenant-cluster by the per-tenant <see cref="PulsarListener"/> at listen time.
    /// </summary>
    public void Compile(PulsarTransport parent)
    {
        Transport.DeadLetterTopic = parent.DeadLetterTopic;
        Transport.RetryLetterTopic = parent.RetryLetterTopic;

        Configure(Transport.Builder);
    }
}
