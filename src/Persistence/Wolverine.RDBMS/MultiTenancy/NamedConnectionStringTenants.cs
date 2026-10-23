using JasperFx.MultiTenancy;
using Wolverine.Configuration;

namespace Wolverine.RDBMS.MultiTenancy;

/// <summary>
/// GH-5127. Static per-tenant databases whose connection strings are resolved <b>by name</b> from
/// <c>IConfiguration</c> -- the way Aspire hands a project one connection string per referenced database --
/// rather than written into the code. Used by the <c>RegisterStaticTenantsUsingNamedConnections(...)</c>
/// extensions of each database-backed message store.
/// </summary>
public class NamedConnectionStringTenants
{
    private readonly List<(string TenantId, string ConnectionStringName)> _tenants = new();

    /// <summary>
    /// Register a tenant database whose connection string is <c>ConnectionStrings:{connectionStringName}</c>
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <param name="connectionStringName">Key of the expected connection string from IConfiguration</param>
    public NamedConnectionStringTenants Register(string tenantId, string connectionStringName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringName);

        _tenants.Add((tenantId, connectionStringName));
        return this;
    }

    public IReadOnlyList<(string TenantId, string ConnectionStringName)> Tenants => _tenants;

    /// <summary>
    /// Declare every tenant's connection string to the aggregated startup check, and register each resolved value
    /// into <paramref name="source"/> before the message store is built.
    /// </summary>
    public void ApplyTo(WolverineOptions options, StaticConnectionStringSource source, string kind,
        string? aspireResourceMethod)
    {
        foreach (var (tenantId, connectionStringName) in _tenants)
        {
            var dependency = new NamedConfigurationDependency(kind, connectionStringName, aspireResourceMethod)
            {
                Usage = $"message store (tenant '{tenantId}')"
            };

            options.ApplyNamedConnectionString(dependency, value => source.Register(tenantId, value));
        }
    }
}
