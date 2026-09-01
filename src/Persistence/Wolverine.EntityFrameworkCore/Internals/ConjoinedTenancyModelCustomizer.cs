using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using JasperFx;
using JasperFx.Core.Reflection;
using JasperFx.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Wolverine.EntityFrameworkCore.Internals;

/// <summary>
///     Model customizer for conjoined multi-tenancy. In addition to the Wolverine
///     envelope storage mapping, every entity implementing
///     JasperFx.MultiTenancy.ITenanted is mapped with a tenant_id column, an index on
///     that column, and a global query filter binding queries to the tenant the
///     DbContext instance is pinned to
/// </summary>
// AOT note (#2746): the tenant query filter is built with expression trees over entity
// types that are statically rooted by the EF model itself; same pattern as the tenanted
// DbContext builders
[UnconditionalSuppressMessage("Trimming", "IL2026",
    Justification = "Entity CLR types and their TenantId property are rooted by the EF Core model. See AOT guide / #2755.")]
[UnconditionalSuppressMessage("Trimming", "IL2072",
    Justification = "Entity CLR types come from the EF Core model and are rooted by it. See AOT guide / #2755.")]
[UnconditionalSuppressMessage("AOT", "IL3050",
    Justification = "LambdaExpression is only built for EF query filters, never compiled to a delegate here. See AOT guide / #2755.")]
public class ConjoinedTenancyModelCustomizer : WolverineModelCustomizer
{
    private static readonly System.Reflection.MethodInfo _tenantIdOf =
        typeof(ConjoinedTenancy).GetMethod(nameof(ConjoinedTenancy.TenantIdOf))!;

    public ConjoinedTenancyModelCustomizer(ModelCustomizerDependencies dependencies) : base(dependencies)
    {
    }

    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);

        var tenantedTypes = modelBuilder.Model.GetEntityTypes()
            .Where(x => x.ClrType.CanBeCastTo<ITenanted>() && !x.IsOwned() && x.BaseType == null)
            .Select(x => x.ClrType)
            .ToArray();

        foreach (var entityType in tenantedTypes)
        {
            var entity = modelBuilder.Entity(entityType);

            entity.Property(nameof(IHasTenantId.TenantId))
                .HasColumnName(StorageConstants.TenantIdColumn)
                .HasDefaultValue(StorageConstants.DefaultTenantId);

            entity.HasIndex(nameof(IHasTenantId.TenantId));

            // The captured DbContext reference below is re-rooted by EF to the context
            // instance executing each query, so the filter always evaluates against the
            // tenant that specific context is pinned to even though the model is cached
            var filter = buildTenantFilter(entityType, context);
#if NET10_0_OR_GREATER
            entity.HasQueryFilter(ConjoinedTenancy.QueryFilterName, filter);
#else
            entity.HasQueryFilter(filter);
#endif
        }

        applyCompositeSagaKeys(modelBuilder, context);
        applyTenantPartitioning(modelBuilder, context);
    }

    // With PartitionPerTenant(), the DATABASE primary key of every partitioned
    // entity becomes composite -- the partition column joins it inside the Weasel
    // table customization (ITenantPartitioning.ApplyToTable) -- but the EF model
    // keeps the user's own single key so FindAsync/Attach call shapes and saga
    // loads are unchanged. Here the model only gains what must exist as a mapped
    // column: SQL Server's int tenant ordinal, stamped by the tenant interceptor
    private static void applyTenantPartitioning(ModelBuilder modelBuilder, DbContext context)
    {
        var options = ConjoinedTenancy.OptionsFor(context.GetType());
        if (!options.PartitioningEnabled)
        {
            return;
        }

        var usesOrdinal = context.Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase)
                          ?? false;
        if (!usesOrdinal)
        {
            return;
        }

        var partitioned = modelBuilder.Model.GetEntityTypes()
            .Where(ConjoinedTenancy.IsPartitionedEntity)
            .ToArray();

        foreach (var entityType in partitioned)
        {
            modelBuilder.Entity(entityType.ClrType)
                .Property<int>(ConjoinedTenancy.TenantOrdinalPropertyName)
                .HasColumnName(options.Partitioning!.TenantOrdinalColumn)
                .ValueGeneratedNever();
        }
    }

    /// <summary>
    /// GH-3542. Promotes a partitioned saga's identity to a real composite <c>(TenantId, Id)</c> key in the
    /// EF model, rather than leaving the composite to exist only in the database as it does for every other
    /// partitioned entity. See <see cref="ConjoinedTenancy.NeedsCompositeModelKey"/> for why sagas are the
    /// exception: their ids are app-assigned, so a db-only composite would let two tenants choose the same
    /// saga id and collide silently.
    ///
    /// <para>
    /// The key order is <c>(Id, TenantId)</c> -- the saga's own id FIRST -- and that ordering is load-bearing
    /// rather than cosmetic. Wolverine determines a saga's id type from
    /// <c>FindPrimaryKey().GetKeyType()</c>, i.e. it assumes the primary key IS the saga id. Leading with the
    /// tenant makes that assumption produce the tenant's type, and the saga id is then assigned into
    /// <c>TenantId</c> -- which surfaces as a CrossTenantWriteException naming a Guid as the tenant. Neither
    /// PostgreSQL nor SQL Server requires the partition column to lead the key, only to be part of it, so
    /// nothing is given up by putting it second.
    /// </para>
    ///
    /// <para>
    /// EF takes composite key values in key order, so <c>LoadEntityFrame</c> emits
    /// <c>FindAsync(sagaId, tenantId)</c> to match. The generated load and this declaration have to agree or
    /// every saga load silently misses.
    /// </para>
    /// </summary>
    private static void applyCompositeSagaKeys(ModelBuilder modelBuilder, DbContext context)
    {
        var options = ConjoinedTenancy.OptionsFor(context.GetType());
        if (!options.PartitioningEnabled)
        {
            return;
        }

        var sagas = modelBuilder.Model.GetEntityTypes()
            .Where(ConjoinedTenancy.NeedsCompositeModelKey)
            .ToArray();

        foreach (var entityType in sagas)
        {
            var existing = entityType.FindPrimaryKey();
            if (existing == null) continue;

            // Whatever the user declared stays the HEAD of the key; only the tenant is appended, so a saga
            // with an unusual key shape is not quietly replaced with an assumed one, and the saga's own id
            // stays the first key property that Wolverine's saga-id determination reads.
            var keyProperties = existing.Properties.Select(x => x.Name)
                .Append(nameof(IHasTenantId.TenantId))
                .Distinct()
                .ToArray();

            modelBuilder.Entity(entityType.ClrType).HasKey(keyProperties);
        }
    }

    private static LambdaExpression buildTenantFilter(Type entityType, DbContext context)
    {
        var parameter = Expression.Parameter(entityType, "e");
        var tenantId = Expression.Property(parameter, nameof(IHasTenantId.TenantId));
        var contextTenantId = Expression.Call(_tenantIdOf, Expression.Constant(context, typeof(DbContext)));

        return Expression.Lambda(Expression.Equal(tenantId, contextTenantId), parameter);
    }
}
