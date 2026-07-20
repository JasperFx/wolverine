using JasperFx.MultiTenancy;
using Wolverine.Http;

namespace ConjoinedMultiTenantedEfCore.Tenants;

public record TenantDirectory(string[] Active, string[] Disabled);

// **7. The wolverine_tenants registry**
//
// Conjoined tenancy keeps an authoritative tenant list in the wolverine_tenants
// table inside the Wolverine durability schema, surfaced through the
// IDynamicTenantSource<string> service that
// AddDbContextWithWolverineManagedConjoinedTenancy() registers. This is the
// same registry that lights up CritterWatch's tenant management UI.
//
// These administrative endpoints operate on the system as a whole rather than
// on any one tenant's data, so they're marked [NotTenanted] to opt out of the
// TenantId.AssertExists() rule in Program.cs
public static class TenantEndpoints
{
    [NotTenanted]
    [WolverineGet("/tenants")]
    public static async Task<TenantDirectory> GetAll(IDynamicTenantSource<string> tenants)
    {
        // Re-read the registry table so this node sees tenants added elsewhere
        await tenants.RefreshAsync();

        var active = tenants.AllActiveByTenant()
            .Select(x => x.TenantId)
            .OrderBy(x => x)
            .ToArray();
        var disabled = (await tenants.AllDisabledAsync()).OrderBy(x => x).ToArray();

        return new TenantDirectory(active, disabled);
    }

    // Registers the tenant in wolverine_tenants. When Weasel-managed
    // partitioning is enabled (see Program.cs), this is also what creates the
    // tenant's physical partition on every ITenanted table
    [NotTenanted]
    [WolverinePost("/tenants/{tenantId}")]
    public static async Task<string> Add(string tenantId, IDynamicTenantSource<string> tenants)
    {
        return await tenants.AddTenantAsync(tenantId, CancellationToken.None);
    }

    // Soft delete: the tenant's rows stay put, but any further work for the
    // tenant is rejected with UnknownTenantIdException until re-enabled
    [NotTenanted]
    [WolverinePost("/tenants/{tenantId}/disable")]
    public static Task Disable(string tenantId, IDynamicTenantSource<string> tenants)
    {
        return tenants.DisableTenantAsync(tenantId);
    }

    [NotTenanted]
    [WolverinePost("/tenants/{tenantId}/enable")]
    public static Task Enable(string tenantId, IDynamicTenantSource<string> tenants)
    {
        return tenants.EnableTenantAsync(tenantId);
    }

    // Hard delete: removes the registry record. With partitioning enabled the
    // tenant's partition -- and every row in it -- is dropped as well
    [NotTenanted]
    [WolverineDelete("/tenants/{tenantId}")]
    public static Task Remove(string tenantId, IDynamicTenantSource<string> tenants)
    {
        return tenants.RemoveTenantAsync(tenantId);
    }
}
