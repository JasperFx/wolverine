using JasperFx.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Wolverine.EntityFrameworkCore.Internals;

/// <summary>
///     SaveChanges interceptor installed by Wolverine's conjoined multi-tenancy that
///     stamps the ambient tenant id onto inserted ITenanted entities and rejects any
///     write against an entity belonging to a different tenant than the one the
///     DbContext is pinned to
/// </summary>
public class TenantStampingInterceptor : SaveChangesInterceptor
{
    public static readonly TenantStampingInterceptor Instance = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        applyTenancyRules(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        applyTenancyRules(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void applyTenancyRules(DbContext? context)
    {
        if (context == null)
        {
            return;
        }

        var contextTenantId = ConjoinedTenancy.TenantIdOf(context);

        if (ConjoinedTenancy.IsTenantDisabled(context.GetType(), contextTenantId))
        {
            // GH-4586: the tenant is registered, it was switched off -- say which
            throw new DisabledTenantException(contextTenantId);
        }

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is not ITenanted tenanted)
            {
                continue;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                case EntityState.Modified:
                case EntityState.Deleted:
                    if (tenanted.TenantId.IsDefaultTenant())
                    {
                        // Unset (or explicitly default) tenant id -- stamp the ambient tenant
                        tenanted.TenantId = contextTenantId;
                    }
                    else if (tenanted.TenantId != contextTenantId)
                    {
                        throw new CrossTenantWriteException(entry.Metadata.ClrType, tenanted.TenantId,
                            contextTenantId, entry.State);
                    }

                    // GH-4612: TenantId is mapped as a concurrency token, so its ORIGINAL value -- not
                    // the value stamped above -- is what lands in the UPDATE/DELETE where clause. For an
                    // entity loaded through the tenant filter the original is already this tenant's id;
                    // for a DETACHED one the snapshot was taken before the stamp and reads null, which
                    // would match no row at all. Pin it either way so a detached write of your own row
                    // works and a detached write of somebody else's cannot.
                    if (entry.State != EntityState.Added)
                    {
                        entry.Property(nameof(ITenanted.TenantId)).OriginalValue = contextTenantId;
                    }

                    stampTenantOrdinal(context, entry, tenanted);

                    break;
            }
        }
    }

    // With PartitionPerTenant() on SQL Server, partitioned rows carry the compact
    // tenant ordinal in a shadow column; the ordinal comes from the pre-hydrated
    // Weasel partition registry
    private static void stampTenantOrdinal(DbContext context,
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, ITenanted tenanted)
    {
        var partitioning = ConjoinedTenancy.PartitioningFor(context.GetType());
        if (partitioning is not { RequiresTenantOrdinalColumn: true })
        {
            return;
        }

        if (entry.Metadata.FindProperty(ConjoinedTenancy.TenantOrdinalPropertyName) == null)
        {
            return;
        }

        if (!partitioning.TryGetOrdinal(tenanted.TenantId!, out var ordinal))
        {
            throw new InvalidOperationException(
                $"No tenant partition is registered for tenant '{tenanted.TenantId}'. Add the tenant first through IConjoinedTenantPartitions<{context.GetType().Name}>.AddTenantAsync()");
        }

        entry.Property(ConjoinedTenancy.TenantOrdinalPropertyName).CurrentValue = ordinal;
    }
}
