using System.Runtime.CompilerServices;
using ImTools;
using JasperFx;
using JasperFx.Core.Reflection;
using JasperFx.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Wolverine.RDBMS.MultiTenancy;

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

    /// <summary>
    ///     Name of the shadow property mapped for the SQL Server tenant ordinal
    ///     column on partitioned entities
    /// </summary>
    public const string TenantOrdinalPropertyName = "TenantOrdinal";

    private static readonly ConditionalWeakTable<DbContext, string> _tenants = new();

    private static ImHashMap<Type, ConjoinedTenancyOptions> _optionsByContextType =
        ImHashMap<Type, ConjoinedTenancyOptions>.Empty;

    private static ImHashMap<Type, ITenantPartitioning> _partitioningByContextType =
        ImHashMap<Type, ITenantPartitioning>.Empty;

    internal static void SetOptions(Type contextType, ConjoinedTenancyOptions options)
    {
        _optionsByContextType = _optionsByContextType.AddOrUpdate(contextType, options);
    }

    internal static bool IsConjoined(Type contextType)
    {
        return _optionsByContextType.TryFind(contextType, out _);
    }

    /// <summary>
    ///     The conjoined tenancy options this DbContext type was registered with
    /// </summary>
    public static ConjoinedTenancyOptions OptionsFor(Type contextType)
    {
        return _optionsByContextType.TryFind(contextType, out var options) ? options : new ConjoinedTenancyOptions();
    }

    internal static void RegisterPartitioning(Type contextType, ITenantPartitioning partitioning)
    {
        _partitioningByContextType = _partitioningByContextType.AddOrUpdate(contextType, partitioning);
    }

    internal static ITenantPartitioning? PartitioningFor(Type contextType)
    {
        return _partitioningByContextType.TryFind(contextType, out var partitioning) ? partitioning : null;
    }

    private static ImHashMap<Type, ImHashMap<string, bool>> _disabledTenantsByContextType =
        ImHashMap<Type, ImHashMap<string, bool>>.Empty;

    internal static void SetDisabledTenants(Type contextType, IEnumerable<string> disabledTenantIds)
    {
        var map = ImHashMap<string, bool>.Empty;
        foreach (var tenantId in disabledTenantIds)
        {
            map = map.AddOrUpdate(tenantId, true);
        }

        _disabledTenantsByContextType = _disabledTenantsByContextType.AddOrUpdate(contextType, map);
    }

    internal static void SetTenantDisabled(Type contextType, string tenantId, bool disabled)
    {
        _disabledTenantsByContextType.TryFind(contextType, out var map);
        map ??= ImHashMap<string, bool>.Empty;
        map = disabled ? map.AddOrUpdate(tenantId, true) : map.Remove(tenantId);
        _disabledTenantsByContextType = _disabledTenantsByContextType.AddOrUpdate(contextType, map);
    }

    internal static bool IsTenantDisabled(Type contextType, string tenantId)
    {
        return _disabledTenantsByContextType.TryFind(contextType, out var map)
               && map.TryFind(tenantId, out var disabled) && disabled;
    }

    internal static bool IsPartitionedEntity(Microsoft.EntityFrameworkCore.Metadata.IReadOnlyEntityType entityType)
    {
        // Sagas stay unpartitioned in this release -- the composite primary key
        // that partitioning requires would change the generated saga load frames
        return entityType.ClrType.CanBeCastTo<ITenanted>()
               && !entityType.ClrType.CanBeCastTo<Saga>()
               && !entityType.IsOwned()
               && entityType.BaseType == null;
    }

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
