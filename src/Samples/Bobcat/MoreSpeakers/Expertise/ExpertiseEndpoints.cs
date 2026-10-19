using System.Linq.Expressions;
using Marten;
using Marten.AspNetCore;
using Marten.Linq;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace Expertise;

// Compiled query — Marten pre-compiles the SQL and query plan once,
// then reuses it for every execution. Combined with WriteArray(),
// the result streams raw JSON from PostgreSQL with zero C# allocation.
public class ActiveExpertiseCategoriesQuery : ICompiledListQuery<ExpertiseCategory>
{
    public Expression<Func<IMartenQueryable<ExpertiseCategory>, IEnumerable<ExpertiseCategory>>> QueryIs()
        => q => q.Where(c => c.IsActive)
                 .OrderBy(c => c.Sector)
                 .ThenBy(c => c.Name);
}

public static class GetExpertiseCategoriesEndpoint
{
    [WolverineGet("/api/expertise")]
    [ProducesResponseType<ExpertiseCategory[]>(200, "application/json")]
    public static Task Get(IQuerySession session, HttpContext context)
        => session.WriteArray(new ActiveExpertiseCategoriesQuery(), context);
}

public static class SearchExpertiseEndpoint
{
    [WolverineGet("/api/expertise/search")]
    public static Task Get(string q, IQuerySession session, HttpContext context)
        => session.Query<ExpertiseCategory>()
            .Where(c => c.IsActive && (
                c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.Skills.Any(s => s.Contains(q, StringComparison.OrdinalIgnoreCase))))
            .WriteArray(context);
}
