using Microsoft.EntityFrameworkCore;

namespace Wolverine.EntityFrameworkCore;

/// <summary>
/// Convenience base class for query plans that return a list of entities.
/// Subclasses override <see cref="Query"/> to build an <see cref="IQueryable{T}"/>;
/// the base class materializes it with
/// <see cref="EntityFrameworkQueryableExtensions.ToListAsync{TSource}(IQueryable{TSource}, CancellationToken)"/>.
/// </summary>
/// <typeparam name="TDbContext">The <see cref="DbContext"/> the plan queries.</typeparam>
/// <typeparam name="TEntity">The entity type returned by the plan.</typeparam>
public abstract class QueryListPlan<TDbContext, TEntity> : IQueryPlan<TDbContext, IReadOnlyList<TEntity>>
    where TDbContext : DbContext
    where TEntity : class
{
    /// <summary>
    /// Build the <see cref="IQueryable{T}"/> the plan represents. Called once per
    /// <see cref="FetchAsync"/> invocation. All LINQ operators (<c>Where</c>,
    /// <c>Include</c>, <c>OrderBy</c>, <c>Select</c>, etc.) are available.
    /// </summary>
    public abstract IQueryable<TEntity> Query(TDbContext dbContext);

    public async Task<IReadOnlyList<TEntity>> FetchAsync(TDbContext dbContext, CancellationToken cancellation)
    {
        return await Query(dbContext).ToListAsync(cancellation).ConfigureAwait(false);
    }
}
