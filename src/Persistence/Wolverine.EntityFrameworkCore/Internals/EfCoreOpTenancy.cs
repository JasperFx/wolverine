using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using JasperFx.Core.Reflection;
using JasperFx.MultiTenancy;
using Microsoft.EntityFrameworkCore;

namespace Wolverine.EntityFrameworkCore.Internals;

/// <summary>
///     Conjoined multi-tenancy rules for the set-based <see cref="EfCoreOp" /> family (GH-4629, and
///     the set-based half of GH-4632).
/// </summary>
/// <remarks>
///     <see cref="TenantStampingInterceptor" /> hooks <c>SavingChanges</c>, and a set-based statement
///     never gets there -- no change tracker entries, no <c>SaveChanges</c>, no interception. Every
///     guard it applies therefore has to be applied again here, by hand, before the statement is
///     composed.
/// </remarks>
// AOT note (#2746): T is an entity type mapped in a registered DbContext, so the EF Core model
// already roots it -- the same justification EfCoreStorageActionApplier next door carries. The
// expression tree below is handed to EF's query translator, never compiled to a delegate.
[UnconditionalSuppressMessage("Trimming", "IL2091",
    Justification = "T is an entity type mapped in a registered DbContext, which roots it. See AOT guide / #2755.")]
[UnconditionalSuppressMessage("Trimming", "IL2026",
    Justification = "Entity CLR types and their TenantId property are rooted by the EF Core model. See AOT guide / #2755.")]
internal static class EfCoreOpTenancy
{
    /// <summary>
    ///     The queryable a set-based operation runs against: the caller's predicate, plus the tenant's
    ///     own predicate when conjoined tenancy is in play.
    /// </summary>
    internal static IQueryable<T> Scope<T>(DbContext dbContext, Expression<Func<T, bool>> predicate,
        bool ignoreQueryFilters) where T : class
    {
        IQueryable<T> query = dbContext.Set<T>();

        if (!IsConjoinedTenanted<T>(dbContext))
        {
            if (ignoreQueryFilters)
            {
                query = query.IgnoreQueryFilters();
            }

            return query.Where(predicate);
        }

        var tenantId = ConjoinedTenancy.TenantIdOf(dbContext);
        AssertTenantIsEnabled(dbContext, tenantId);

        if (ignoreQueryFilters)
        {
            throw new CrossTenantWriteException(typeof(T), tenantId,
                "IgnoreQueryFilters() would drop the tenant filter, which is the only thing keeping the statement inside this tenant. Scope the operation with its own predicate instead.");
        }

        // Belt and braces with the global query filter: the filter is what EF applies to an
        // ExecuteUpdate/ExecuteDelete today, and this makes the tenant predicate part of the
        // operation itself rather than a property of how the DbContext happens to be configured.
        return query.Where(predicate).Where(tenantPredicate<T>(tenantId));
    }

    /// <summary>
    ///     Refuses <c>SetProperty(x =&gt; x.TenantId, ...)</c>, which is how a set-based update moves
    ///     rows from one tenant to another -- the thing <see cref="CrossTenantWriteException" /> exists
    ///     to prevent on every other write path.
    /// </summary>
    internal static void AssertSettersDoNotMoveTenant<T>(DbContext dbContext, EfCoreSetters<T> setters)
        where T : class
    {
        if (!IsConjoinedTenanted<T>(dbContext)) return;

        if (setters.Setters.Any(x => namesTenantId(x.Property)))
        {
            throw new CrossTenantWriteException(typeof(T), ConjoinedTenancy.TenantIdOf(dbContext),
                $"The update sets {nameof(ITenanted.TenantId)}, which would move rows into another tenant.");
        }
    }

    private static bool namesTenantId(LambdaExpression property)
    {
        var body = property.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert
            ? convert.Operand
            : property.Body;

        return body is MemberExpression member && member.Member.Name == nameof(ITenanted.TenantId);
    }

    internal static bool IsConjoinedTenanted<T>(DbContext dbContext)
    {
        return typeof(T).CanBeCastTo<ITenanted>() && ConjoinedTenancy.IsConjoined(dbContext.GetType());
    }

    /// <summary>
    ///     GH-4586 for a raw-SQL operation, which has no entity type to reason about but still runs
    ///     against a tenant's database.
    /// </summary>
    internal static void AssertContextTenantIsEnabled(DbContext dbContext)
    {
        AssertTenantIsEnabled(dbContext, ConjoinedTenancy.TenantIdOf(dbContext));
    }

    /// <summary>
    ///     GH-4586, for the path that has no <c>SaveChanges</c> to intercept.
    /// </summary>
    internal static void AssertTenantIsEnabled(DbContext dbContext, string tenantId)
    {
        if (ConjoinedTenancy.IsTenantDisabled(dbContext.GetType(), tenantId))
        {
            throw new DisabledTenantException(tenantId);
        }
    }

    private static Expression<Func<T, bool>> tenantPredicate<T>(string tenantId)
    {
        var parameter = Expression.Parameter(typeof(T), "e");
        var property = Expression.Property(parameter, nameof(ITenanted.TenantId));

        return Expression.Lambda<Func<T, bool>>(
            Expression.Equal(property, Expression.Constant(tenantId, typeof(string))), parameter);
    }
}
