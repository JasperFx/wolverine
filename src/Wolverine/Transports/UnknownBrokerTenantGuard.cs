using ImTools;
using Wolverine.Configuration;
using Wolverine.Transports.Sending;

namespace Wolverine.Transports;

/// <summary>
///     GH-5196, the receiving side of <see cref="TenantedIdBehavior.DeadLetterUnknownTenants" />. A tenant-aware broker
///     endpoint dead letters an incoming message whose tenant id no tenant registered with the transport matches,
///     with the same <see cref="UnknownBrokerTenantException" /> the sending side uses, instead of handling it as a
///     tenant nobody registered. The tenant listeners stamp their own (registered) tenant id, so in practice this
///     catches a message that reached the default broker carrying a tenant id header. Checked once per message at the
///     top of the endpoint's handler pipeline, so the message takes the endpoint's own dead letter path (native dead
///     letter queue or the durable inbox) and tracked sessions see it as moved to the error queue.
/// </summary>
internal sealed class UnknownBrokerTenantGuard
{
    private readonly Uri _endpoint;
    private readonly ImHashMap<string, bool> _tenants;

    private UnknownBrokerTenantGuard(Uri endpoint, IEnumerable<string> tenantIds)
    {
        _endpoint = endpoint;
        var tenants = ImHashMap<string, bool>.Empty;
        foreach (var tenantId in tenantIds)
        {
            tenants = tenants.AddOrUpdate(tenantId, true);
        }

        _tenants = tenants;
    }

    /// <summary>
    ///     Called by a transport as it builds the listener for a tenant-aware endpoint. Only
    ///     <see cref="TenantedIdBehavior.DeadLetterUnknownTenants" /> guards the endpoint; every other behavior
    ///     receives as Wolverine 6.x did
    /// </summary>
    public static void Install(Endpoint endpoint, TenantedIdBehavior behavior, IEnumerable<string> tenantIds)
    {
        endpoint.UnknownTenantGuard = behavior == TenantedIdBehavior.DeadLetterUnknownTenants
            ? new UnknownBrokerTenantGuard(endpoint.Uri, tenantIds)
            : null;
    }

    /// <summary>
    ///     The reason to dead letter <paramref name="envelope" />, or null to handle it. Tenant ids match exactly
    ///     (ordinal, case sensitive, no trimming), the same rule the sending side uses, and no tenant id or the default
    ///     tenant's id means "no tenant"
    /// </summary>
    public UnknownBrokerTenantException? Check(Envelope envelope)
    {
        var tenantId = envelope.TenantId;
        if (TenantedSender.IsUntenanted(tenantId)) return null;
        if (_tenants.TryFind(tenantId!, out _)) return null;

        return UnknownBrokerTenantException.Received(tenantId!, envelope.MessageType, _endpoint);
    }
}
