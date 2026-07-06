using DotPulsar;
using DotPulsar.Abstractions;
using Wolverine.Pulsar.Internal;
using Wolverine.Transports.Sending;

namespace Wolverine.Pulsar;

/// <summary>
/// Transport-wide configuration for the Pulsar integration, returned from
/// <see cref="PulsarTransportExtensions.UsePulsar(Wolverine.WolverineOptions,System.Action{DotPulsar.Abstractions.IPulsarClientBuilder})"/>.
/// Use it to set transport-level defaults that individual endpoints inherit unless they override them.
/// </summary>
public class PulsarConfiguration
{
    private readonly PulsarTransport _transport;

    internal PulsarConfiguration(PulsarTransport transport)
    {
        _transport = transport;
    }

    internal PulsarTransport Transport => _transport;

    /// <summary>
    /// Set the transport-wide default dead letter topic applied to every Pulsar endpoint that does
    /// not configure its own via <c>ListenToPulsarTopic(...).DeadLetterQueueing(...)</c>. Per-endpoint
    /// configuration always wins (see <see cref="PulsarEndpoint.EffectiveDeadLetterTopic"/>). Mirrors
    /// the Kafka transport-level dead letter default + per-endpoint override shape.
    /// </summary>
    public PulsarConfiguration DeadLetterQueueing(DeadLetterTopic deadLetterTopic)
    {
        _transport.DeadLetterTopic = deadLetterTopic ?? throw new ArgumentNullException(nameof(deadLetterTopic));
        return this;
    }

    /// <summary>
    /// Set the transport-wide default retry letter topic applied to every Pulsar endpoint that does
    /// not configure its own. Per-endpoint configuration always wins (see
    /// <see cref="PulsarEndpoint.EffectiveRetryLetterTopic"/>).
    /// </summary>
    public PulsarConfiguration RetryLetterQueueing(RetryLetterTopic retryLetterTopic)
    {
        _transport.RetryLetterTopic = retryLetterTopic ?? throw new ArgumentNullException(nameof(retryLetterTopic));
        return this;
    }

    /// <summary>
    /// Register a Wolverine tenant that is served by its own dedicated Pulsar <b>cluster</b> (GH-3308), while
    /// sharing the topic topology declared on this transport. Outbound messages whose
    /// <see cref="Envelope.TenantId"/> matches <paramref name="tenantId"/> are routed to that cluster; inbound
    /// messages consumed from it are stamped with the tenant id.
    ///
    /// <para>
    /// ⚠️ <b>This is NOT the native Pulsar tenant.</b> Pulsar has its own "tenant" segment in the
    /// <c>persistent://{tenant}/{namespace}/{topic}</c> topic hierarchy; that is unchanged and shared across all
    /// Wolverine tenants. The per-tenant differentiator here is the <em>cluster connection</em> — the service
    /// URL and authentication you set on <paramref name="configure"/> — never the native tenant segment.
    /// </para>
    ///
    /// <para>
    /// DotPulsar's client builder is not trivially cloneable, so <paramref name="configure"/> runs against a
    /// fresh <c>PulsarClient.Builder()</c> and <b>must fully specify</b> the tenant cluster's <c>ServiceUrl</c>
    /// and any authentication (the same "fresh factory" caveat as the RabbitMQ transport).
    /// </para>
    /// </summary>
    /// <param name="tenantId">The Wolverine tenant id (matched against <see cref="Envelope.TenantId"/>).</param>
    /// <param name="configure">Fully configures the tenant cluster's DotPulsar client (ServiceUrl + auth).</param>
    public PulsarConfiguration AddTenant(string tenantId, Action<IPulsarClientBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(configure);

        _transport.Tenants[tenantId] = new PulsarTenant(tenantId) { Configure = configure };
        return this;
    }

    /// <summary>
    /// Convenience overload of <see cref="AddTenant(string,System.Action{DotPulsar.Abstractions.IPulsarClientBuilder})"/>
    /// for the common case where the tenant cluster differs only by service URL (no extra auth). Registers a
    /// Wolverine tenant served by the Pulsar cluster at <paramref name="serviceUrl"/>.
    ///
    /// <para>⚠️ As with the action overload, <paramref name="serviceUrl"/> selects a <em>cluster</em>, not the
    /// native Pulsar tenant segment.</para>
    /// </summary>
    /// <param name="tenantId">The Wolverine tenant id (matched against <see cref="Envelope.TenantId"/>).</param>
    /// <param name="serviceUrl">The Pulsar service URL for the tenant's cluster, e.g. <c>pulsar://tenant-a:6650</c>.</param>
    public PulsarConfiguration AddTenant(string tenantId, Uri serviceUrl)
    {
        ArgumentNullException.ThrowIfNull(serviceUrl);
        return AddTenant(tenantId, builder => builder.ServiceUrl(serviceUrl));
    }

    /// <summary>
    /// Control how outbound messages whose <see cref="Envelope.TenantId"/> is null or does not match a
    /// registered tenant are handled when broker-per-tenant multi-tenancy is in effect (GH-3308). Default is
    /// <see cref="Wolverine.Transports.Sending.TenantedIdBehavior.FallbackToDefault"/> (route to the default
    /// broker configured via <c>UsePulsar</c>).
    /// </summary>
    public PulsarConfiguration TenantIdBehavior(TenantedIdBehavior behavior)
    {
        _transport.TenantedIdBehavior = behavior;
        return this;
    }
}
