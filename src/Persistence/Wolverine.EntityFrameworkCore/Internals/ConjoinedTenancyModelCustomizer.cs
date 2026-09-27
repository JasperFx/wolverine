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
                .HasDefaultValue(StorageConstants.DefaultTenantId)
                // GH-4612. The query filter does not apply to SaveChanges, so without this an UPDATE or
                // DELETE matched on the primary key alone and a DETACHED entity -- one built from a
                // message or a request body, whose TenantId is null only because it was never loaded --
                // reached whatever tenant happened to own that id. As a concurrency token, tenant_id
                // joins the where clause of every update and delete, and TenantStampingInterceptor pins
                // its ORIGINAL value to the context's tenant, so the statement can only ever match a row
                // this tenant owns. A cross-tenant attempt matches nothing and EF reports it the way it
                // reports any zero-row write, with DbUpdateConcurrencyException.
                .IsConcurrencyToken();

            entity.HasIndex(nameof(IHasTenantId.TenantId));

            // The captured DbContext reference below is re-rooted by EF to the context
            // instance executing each query, so the filter always evaluates against the
            // tenant that specific context is pinned to even though the model is cached
            var filter = buildTenantFilter(entityType, context);

            // GH-4632. This customizer runs AFTER the user's OnModelCreating, so an ITenanted entity
            // may already carry a query filter the user declared themselves -- a soft delete filter,
            // typically. Both EF generations punish assigning ours on top of it:
            //
            //   * EF 9 has exactly ONE anonymous filter slot per entity type and the last writer
            //     wins, so the user's filter was silently discarded. Soft-deleted rows came back
            //     and nothing warned.
            //   * EF 10 added NAMED filters but forbids mixing: an anonymous filter and a named one
            //     on the same entity type throws "Both anonymous and named query filters cannot be
            //     applied simultaneously" while the model is built, which fails the whole DbContext
            //     at startup.
            //
            // Composing into the user's own anonymous filter is the one shape both generations
            // accept, and it keeps IgnoreQueryFilters() meaning exactly what it always meant.
#if NET10_0_OR_GREATER
            var anonymous = entity.Metadata.GetDeclaredQueryFilters()
                .FirstOrDefault(x => x.IsAnonymous)?.Expression;
            if (anonymous == null)
            {
                entity.HasQueryFilter(ConjoinedTenancy.QueryFilterName, filter);
            }
            else
            {
                entity.HasQueryFilter(combine(anonymous, filter));
            }
#else
            var existing = entity.Metadata.GetQueryFilter();
            entity.HasQueryFilter(existing == null ? filter : combine(existing, filter));
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

    // GH-4632. The two filters were written independently and therefore have DIFFERENT
    // ParameterExpression instances for the same entity type. EF matches parameters by
    // reference, so the tenant predicate has to be re-bound onto the user's parameter
    // before the two bodies can be ANDed into one lambda
    private static LambdaExpression combine(LambdaExpression existing, LambdaExpression tenantFilter)
    {
        var parameter = existing.Parameters[0];
        var rebound = new ParameterRebinder(tenantFilter.Parameters[0], parameter).Visit(tenantFilter.Body)!;

        return Expression.Lambda(Expression.AndAlso(existing.Body, rebound), parameter);
    }

    private sealed class ParameterRebinder : ExpressionVisitor
    {
        private readonly ParameterExpression _from;
        private readonly ParameterExpression _to;

        public ParameterRebinder(ParameterExpression from, ParameterExpression to)
        {
            _from = from;
            _to = to;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            return ReferenceEquals(node, _from) ? _to : base.VisitParameter(node);
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
