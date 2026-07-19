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

    private static LambdaExpression buildTenantFilter(Type entityType, DbContext context)
    {
        var parameter = Expression.Parameter(entityType, "e");
        var tenantId = Expression.Property(parameter, nameof(IHasTenantId.TenantId));
        var contextTenantId = Expression.Call(_tenantIdOf, Expression.Constant(context, typeof(DbContext)));

        return Expression.Lambda(Expression.Equal(tenantId, contextTenantId), parameter);
    }
}
