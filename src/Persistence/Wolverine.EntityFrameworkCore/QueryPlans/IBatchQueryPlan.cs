using Microsoft.EntityFrameworkCore;
using Weasel.EntityFrameworkCore.Batching;

namespace Wolverine.EntityFrameworkCore;

/// <summary>
/// A query plan that can execute inside a Weasel <see cref="BatchedQuery"/>
/// batch — multiple batch-capable plans on the same handler are grouped
/// into a single database round-trip by Wolverine's code generation.
/// <para>
/// This is the EF Core counterpart to Marten's <c>IBatchQueryPlan&lt;T&gt;</c>.
/// When a type implements both <see cref="IQueryPlan{TDbContext,TResult}"/>
/// (standalone fetch) and <see cref="IBatchQueryPlan{TDbContext,TResult}"/>
/// (batched fetch), Wolverine prefers the batched path whenever two or more
/// batchable loads appear on the same handler — mirroring the pattern used
/// by <see cref="QueryPlan{TDbContext,TEntity}"/> and
/// <see cref="QueryListPlan{TDbContext,TEntity}"/>.
/// </para>
/// </summary>
/// <typeparam name="TDbContext">The <see cref="DbContext"/> the plan queries.</typeparam>
/// <typeparam name="TResult">The result type the plan returns.</typeparam>
public interface IBatchQueryPlan<in TDbContext, TResult> where TDbContext : DbContext
{
    /// <summary>
    /// Enlist this plan into the supplied <see cref="BatchedQuery"/> and
    /// return the pending <see cref="Task{TResult}"/>. The task resolves when
    /// the batch executes (via <see cref="BatchedQuery.ExecuteAsync"/>).
    /// </summary>
    Task<TResult> FetchAsync(BatchedQuery batch, TDbContext dbContext);
}
