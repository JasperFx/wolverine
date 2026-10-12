using NATS.Client.Core;
using Wolverine.Nats.Configuration;

namespace Wolverine.Nats.Internal;

public class NatsTenant
{
    public NatsTenant(string tenantId)
    {
        TenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
    }

    public string TenantId { get; }
    public ITenantSubjectMapper? SubjectMapper { get; set; }

    /// <summary>
    /// The tenant's own connection configuration (full auth / TLS surface, via
    /// <see cref="NatsTransportConfiguration.ToNatsOpts"/>) when it should use a dedicated NATS connection
    /// rather than the shared transport connection. Null means subject-prefix isolation on the shared connection.
    /// For a tenant added with a configuration action, the transport builds it when it connects: a copy of the
    /// transport's configuration as it is then, with the action applied.
    /// </summary>
    public NatsTransportConfiguration? ConnectionConfiguration { get; set; }

    /// <summary>
    /// The action <c>AddTenant(tenantId, cfg =&gt; ...)</c> registered, run against a copy of the transport's
    /// configuration when the transport connects, so it sees every transport setting made after the call
    /// </summary>
    internal Action<NatsTransportConfiguration>? ConfigureConnection { get; set; }

    /// <summary>
    /// The tenant's dedicated NATS connection when <see cref="HasOwnConnection"/> is true. Created and owned
    /// by the transport during its ConnectAsync, and disposed with the transport. Null when the tenant reuses
    /// the shared transport connection.
    /// </summary>
    internal NatsConnection? Connection { get; set; }

    /// <summary>
    /// True when this tenant declares its own connection configuration, and therefore gets a dedicated NATS
    /// connection rather than sharing the transport's connection.
    /// </summary>
    public bool HasOwnConnection => ConnectionConfiguration != null || ConfigureConnection != null;
}
