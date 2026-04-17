using Microsoft.EntityFrameworkCore;

namespace Wolverine.EntityFrameworkCore;

/// <summary>
/// Convenience base class for query plans that return a single matching entity
/// (or <c>null</c>). Subclasses override <see cref="Query"/> to build an
/// <see cref="IQueryable{T}"/>; the base class materializes it with
/// <see cref="EntityFrameworkQueryableExtensions.FirstOrDefaultAsync{TSource}(IQueryable{TSource}, CancellationToken)"/>.
/// </summary>
/// <typeparam name="TDbContext">The <see cref="DbContext"/> the plan queries.</typeparam>
/// <typeparam name="TEntity">The entity type returned by the plan.</typeparam>
public abstract class QueryPlan<TDbContext, TEntity> : IQueryPlan<TDbContext, TEntity?>
    where TDbContext : DbContext
    where TEntity : class
{
    /// <summary>
    /// Build the <see cref="IQueryable{T}"/> the plan represents. Called once per
    /// <see cref="FetchAsync"/> invocation. All LINQ operators (<c>Where</c>,
    /// <c>Include</c>, <c>OrderBy</c>, <c>Select</c>, etc.) are available.
    /// </summary>
    public abstract IQueryable<TEntity> Query(TDbContext dbContext);

    public async Task<TEntity?> FetchAsync(TDbContext dbContext, CancellationToken cancellation)
    {
        return await Query(dbContext).FirstOrDefaultAsync(cancellation).ConfigureAwait(false);
    }
}
