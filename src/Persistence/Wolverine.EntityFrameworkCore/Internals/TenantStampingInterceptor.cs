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

                    break;
            }
        }
    }
}
