using System.Runtime.CompilerServices;
using JasperFx;
using JasperFx.MultiTenancy;
using Microsoft.EntityFrameworkCore;

namespace Wolverine.EntityFrameworkCore.Internals;

/// <summary>
///     Tracks the tenant id that a conjoined-multi-tenant DbContext instance is pinned
///     to. The <see cref="ConjoinedDbContextBuilder{T}" /> pins every context it builds,
///     and both the tenant global query filter and the
///     <see cref="TenantStampingInterceptor" /> read the pinned value back through
///     <see cref="TenantIdOf" />.
/// </summary>
public static class ConjoinedTenancy
{
    /// <summary>
    ///     Name of the global query filter that Wolverine attaches to every
    ///     ITenanted entity of a conjoined-multi-tenant DbContext (named filters
    ///     require EF Core 10+)
    /// </summary>
    public const string QueryFilterName = "wolverine_conjoined_tenancy";

    private static readonly ConditionalWeakTable<DbContext, string> _tenants = new();

    internal static void Pin(DbContext context, string? tenantId)
    {
        _tenants.AddOrUpdate(context, tenantId.IsDefaultTenant() ? StorageConstants.DefaultTenantId : tenantId!);
    }

    /// <summary>
    ///     The tenant id this DbContext instance is pinned to, or
    ///     StorageConstants.DefaultTenantId when the context was not built through
    ///     Wolverine's conjoined tenancy (e.g. contexts resolved for EF Core migrations)
    /// </summary>
    public static string TenantIdOf(DbContext context)
    {
        return _tenants.TryGetValue(context, out var tenantId) ? tenantId : StorageConstants.DefaultTenantId;
    }
}
