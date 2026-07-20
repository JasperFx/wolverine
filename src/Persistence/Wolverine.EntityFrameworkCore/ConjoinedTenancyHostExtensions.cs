using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.EntityFrameworkCore.Internals;

namespace Wolverine.EntityFrameworkCore;

/// <summary>
/// Convenience admin API for Wolverine-managed conjoined EF Core multi-tenancy, mirroring Marten's
/// AddMartenManagedTenantsAsync / RemoveMartenManagedTenantsAsync family (GH-3537). These batch the
/// per-tenant registry operations that previously had to be done one id at a time through the
/// resolved IDynamicTenantSource&lt;string&gt;.
/// </summary>
public static class ConjoinedTenancyHostExtensions
{
    /// <summary>
    /// Register the given tenant ids for the conjoined <typeparamref name="T"/> DbContext in the
    /// wolverine_tenants registry (creating the per-tenant partition when partitioning is enabled).
    /// Returns the registered tenant ids as stored (they are normalized to the configured
    /// <c>TenantIdStyle</c>).
    /// </summary>
    public static Task<IReadOnlyList<string>> AddWolverineManagedTenantsAsync<T>(this IHost host,
        params string[] tenantIds) where T : DbContext
    {
        return host.AddWolverineManagedTenantsAsync<T>(CancellationToken.None, tenantIds);
    }

    /// <summary>
    /// Register the given tenant ids for the conjoined <typeparamref name="T"/> DbContext in the
    /// wolverine_tenants registry (creating the per-tenant partition when partitioning is enabled).
    /// Returns the registered tenant ids as stored (they are normalized to the configured
    /// <c>TenantIdStyle</c>).
    /// </summary>
    public static async Task<IReadOnlyList<string>> AddWolverineManagedTenantsAsync<T>(this IHost host,
        CancellationToken token, params string[] tenantIds) where T : DbContext
    {
        var source = host.Services.GetRequiredService<ConjoinedTenantSource<T>>();

        var results = new List<string>(tenantIds.Length);
        foreach (var tenantId in tenantIds)
        {
            results.Add(await source.AddTenantAsync(tenantId, token));
        }

        return results;
    }

    /// <summary>
    /// Remove the given tenant ids for the conjoined <typeparamref name="T"/> DbContext from the
    /// wolverine_tenants registry (dropping the per-tenant partition and its data when partitioning
    /// is enabled).
    /// </summary>
    public static async Task RemoveWolverineManagedTenantsAsync<T>(this IHost host,
        params string[] tenantIds) where T : DbContext
    {
        var source = host.Services.GetRequiredService<ConjoinedTenantSource<T>>();

        foreach (var tenantId in tenantIds)
        {
            await source.RemoveTenantAsync(tenantId);
        }
    }
}
