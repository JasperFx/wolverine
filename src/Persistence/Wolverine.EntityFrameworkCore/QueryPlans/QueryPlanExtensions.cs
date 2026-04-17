using Microsoft.EntityFrameworkCore;

namespace Wolverine.EntityFrameworkCore;

/// <summary>
/// Convenience extension methods for executing <see cref="IQueryPlan{TDbContext,TResult}"/>
/// instances against a <see cref="DbContext"/>. Parallels Marten's
/// <c>IQuerySession.QueryByPlanAsync</c>.
/// </summary>
public static class QueryPlanExtensions
{
    /// <summary>
    /// Execute a query plan against this <see cref="DbContext"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// var order = await db.QueryByPlanAsync(new ActiveOrderForCustomer(customerId), ct);
    /// </code>
    /// </example>
    public static Task<TResult> QueryByPlanAsync<TDbContext, TResult>(
        this TDbContext dbContext,
        IQueryPlan<TDbContext, TResult> plan,
        CancellationToken cancellation = default)
        where TDbContext : DbContext
    {
        if (dbContext == null) throw new ArgumentNullException(nameof(dbContext));
        if (plan == null) throw new ArgumentNullException(nameof(plan));

        return plan.FetchAsync(dbContext, cancellation);
    }
}
