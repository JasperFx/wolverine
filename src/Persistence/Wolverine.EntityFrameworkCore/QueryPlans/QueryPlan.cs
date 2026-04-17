using Microsoft.EntityFrameworkCore;
using Weasel.EntityFrameworkCore.Batching;

namespace Wolverine.EntityFrameworkCore;

/// <summary>
/// Convenience base class for query plans that return a single matching entity
/// (or <c>null</c>). Subclasses override <see cref="Query"/> to build an
/// <see cref="IQueryable{T}"/>; the base class materializes it with
/// <see cref="EntityFrameworkQueryableExtensions.FirstOrDefaultAsync{TSource}(IQueryable{TSource}, CancellationToken)"/>
/// in the standalone path and <see cref="BatchedQuery.QuerySingle{T}"/> when batched.
/// <para>
/// Implements both <see cref="IQueryPlan{TDbContext,TResult}"/> and
/// <see cref="IBatchQueryPlan{TDbContext,TResult}"/>, so Wolverine can choose the
/// batched path whenever multiple batchable loads appear on the same handler.
/// </para>
/// </summary>
/// <typeparam name="TDbContext">The <see cref="DbContext"/> the plan queries.</typeparam>
/// <typeparam name="TEntity">The entity type returned by the plan.</typeparam>
public abstract class QueryPlan<TDbContext, TEntity>
    : IQueryPlan<TDbContext, TEntity?>, IBatchQueryPlan<TDbContext, TEntity?>
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

    public async Task<TEntity?> FetchAsync(TDbContext dbContext, CancellationToken cancellation)
    {
        return await Query(dbContext).FirstOrDefaultAsync(cancellation).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<TEntity?> FetchAsync(BatchedQuery batch, TDbContext dbContext)
    {
        return batch.QuerySingle(Query(dbContext));
    }
}
