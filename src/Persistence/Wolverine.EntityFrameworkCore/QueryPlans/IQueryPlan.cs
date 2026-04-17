using Microsoft.EntityFrameworkCore;

namespace Wolverine.EntityFrameworkCore;

/// <summary>
/// A reusable, testable unit of query logic over an Entity Framework Core
/// <see cref="DbContext"/> — Wolverine's adaptation of Marten's
/// <c>IQueryPlan</c> and a first-class implementation of the
/// <see href="https://specification.ardalis.com/">Specification pattern</see>
/// for EF Core.
/// <para>
/// A query plan encapsulates query logic in its own class so handlers can
/// consume complex reads without reaching for a repository/adapter layer.
/// Pass parameters through the constructor and execute the plan against any
/// <see cref="DbContext"/> instance Wolverine provides to the handler (including
/// tenanted ones).
/// </para>
/// </summary>
/// <typeparam name="TDbContext">The <see cref="DbContext"/> the plan queries.</typeparam>
/// <typeparam name="TResult">The result type the plan returns.</typeparam>
public interface IQueryPlan<in TDbContext, TResult> where TDbContext : DbContext
{
    /// <summary>
    /// Execute the query plan and return its result.
    /// </summary>
    Task<TResult> FetchAsync(TDbContext dbContext, CancellationToken cancellation);
}
