using Marten;
using Wolverine.Http;

namespace WolverineWebApi;

public record SearchRequest(string Term, int Page);

public record SearchResults(string Term, int Page, string[] Hits);

public record SearchAudit(Guid Id, string Term);

public static class QueryEndpoints
{
    #region sample_wolverine_query_endpoint
    // QUERY (RFC 10008) is safe and idempotent like GET, but carries a request body — ideal for
    // search endpoints whose criteria are too large or structured for the query string. Wolverine
    // binds the request body just like it would for POST. Note that Wolverine's middleware rules
    // are not verb-aware: this endpoint stays free of transactional middleware because it takes
    // no IDocumentSession/DbContext dependency, not because it is a QUERY.
    [WolverineQuery("/search")]
    public static SearchResults Search(SearchRequest request)
    {
        var hits = Enumerable.Range(1, request.Page)
            .Select(i => $"{request.Term}-{i}")
            .ToArray();

        return new SearchResults(request.Term, request.Page, hits);
    }

    #endregion

    // Middleware rules are dependency-based, not verb-based: taking an IDocumentSession attracts
    // AutoApplyTransactions on a QUERY endpoint exactly as it would on a POST.
    [WolverineQuery("/search/audited")]
    public static SearchResults SearchAudited(SearchRequest request, IDocumentSession session)
    {
        session.Store(new SearchAudit(Guid.NewGuid(), request.Term));
        return new SearchResults(request.Term, request.Page, []);
    }

    // IQuerySession is Marten's read-only session and does not trigger transactional middleware,
    // so this is the right dependency for a QUERY endpoint that reads the database.
    [WolverineQuery("/search/readonly")]
    public static SearchResults SearchReadonly(SearchRequest request, IQuerySession session)
    {
        return new SearchResults(request.Term, request.Page, []);
    }
}
