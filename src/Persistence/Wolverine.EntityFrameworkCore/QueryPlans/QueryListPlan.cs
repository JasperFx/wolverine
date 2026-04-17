using Microsoft.EntityFrameworkCore;
using Weasel.EntityFrameworkCore.Batching;

namespace Wolverine.EntityFrameworkCore;

/// <summary>
/// Convenience base class for query plans that return a list of entities.
/// Subclasses override <see cref="Query"/> to build an <see cref="IQueryable{T}"/>;
/// the base class materializes it with
/// <see cref="EntityFrameworkQueryableExtensions.ToListAsync{TSource}(IQueryable{TSource}, CancellationToken)"/>
/// in the standalone path and <see cref="BatchedQuery.Query{T}"/> when batched.
/// <para>
/// Implements both <see cref="IQueryPlan{TDbContext,TResult}"/> and
/// <see cref="IBatchQueryPlan{TDbContext,TResult}"/>, so Wolverine can choose the
/// batched path whenever multiple batchable loads appear on the same handler.
/// </para>
/// </summary>
/// <typeparam name="TDbContext">The <see cref="DbContext"/> the plan queries.</typeparam>
/// <typeparam name="TEntity">The entity type returned by the plan.</typeparam>
public abstract class QueryListPlan<TDbContext, TEntity>
    : IQueryPlan<TDbContext, IReadOnlyList<TEntity>>, IBatchQueryPlan<TDbContext, IReadOnlyList<TEntity>>
    where TDbContext : DbContext
    where TEntity : class, new()
{
    /// <summary>
    /// Build the <see cref="IQueryable{T}"/> the plan represents. Called once per
    /// <see cref="FetchAsync(TDbContext, CancellationToken)"/> invocation. All LINQ
    /// operators (<c>Where</c>, <c>Include</c>, <c>OrderBy</c>, <c>Select</c>, etc.)
    /// are available.
    /// </summary>
    public abstract IQueryable<TEntity> Query(TDbContext dbContext);

    public async Task<IReadOnlyList<TEntity>> FetchAsync(TDbContext dbContext, CancellationToken cancellation)
    {
        return await Query(dbContext).ToListAsync(cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TEntity>> FetchAsync(BatchedQuery batch, TDbContext dbContext)
    {
        return batch.Query(Query(dbContext));
    }
}
