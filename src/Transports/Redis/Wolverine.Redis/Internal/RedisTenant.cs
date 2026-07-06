using StackExchange.Redis;

namespace Wolverine.Redis.Internal;

/// <summary>
/// Describes a tenant that talks to its own dedicated Redis server (broker-per-tenant). Exactly one of the
/// connection sources — connection string, <see cref="ConfigurationOptions"/>, or a caller-managed
/// <see cref="IConnectionMultiplexer"/> — is populated when the tenant has its own connection. Mirrors
/// <c>NatsTenant</c>. GH-3309.
/// </summary>
public class RedisTenant
{
    public RedisTenant(string tenantId)
    {
        TenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
    }

    public string TenantId { get; }

    /// <summary>
    /// The tenant's own connection string when it should use a dedicated Redis connection.
    /// </summary>
    internal string? ConnectionString { get; set; }

    /// <summary>
    /// The tenant's own <see cref="ConfigurationOptions"/> when it should use a dedicated Redis connection.
    /// </summary>
    internal ConfigurationOptions? ConfigurationOptions { get; set; }

    /// <summary>
    /// A caller-managed multiplexer for the tenant. Wolverine uses it as-is and never disposes it.
    /// </summary>
    internal IConnectionMultiplexer? ExternalConnection { get; set; }

    /// <summary>
    /// The tenant's dedicated multiplexer when <see cref="HasOwnConnection"/> is true. Lazily created and
    /// cached by <c>RedisTransport.GetTenantConnection</c>, and disposed with the transport when
    /// <see cref="OwnsConnection"/> is true. Null when the tenant reuses the shared transport connection.
    /// </summary>
    internal IConnectionMultiplexer? Connection { get; set; }

    /// <summary>
    /// True when this tenant declares its own connection, and therefore gets a dedicated Redis connection
    /// rather than sharing the transport's connection.
    /// </summary>
    public bool HasOwnConnection =>
        ConnectionString != null || ConfigurationOptions != null || ExternalConnection != null;

    /// <summary>
    /// True when Wolverine built the tenant's multiplexer itself (from a connection string /
    /// ConfigurationOptions) and is therefore responsible for disposing it. A caller-managed multiplexer is
    /// owned elsewhere and must not be disposed.
    /// </summary>
    internal bool OwnsConnection => ExternalConnection == null;

    /// <summary>
    /// Build the tenant's dedicated multiplexer from whichever connection source is populated.
    /// </summary>
    internal IConnectionMultiplexer BuildConnection()
    {
        if (ExternalConnection != null) return ExternalConnection;
        if (ConfigurationOptions != null) return ConnectionMultiplexer.Connect(ConfigurationOptions);
        return ConnectionMultiplexer.Connect(ConnectionString!);
    }
}
