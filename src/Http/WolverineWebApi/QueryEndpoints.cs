using Wolverine.Http;

namespace WolverineWebApi;

public record SearchRequest(string Term, int Page);

public record SearchResults(string Term, int Page, string[] Hits);

public static class QueryEndpoints
{
    #region sample_wolverine_query_endpoint
    // QUERY (RFC 10008) is safe and idempotent like GET, but carries a request body — ideal for
    // search endpoints whose criteria are too large or structured for the query string. Wolverine
    // binds the request body just like it would for POST, and — because the endpoint takes no
    // message-bus dependency — no transactional/outbox middleware is applied.
    [WolverineQuery("/search")]
    public static SearchResults Search(SearchRequest request)
    {
        var hits = Enumerable.Range(1, request.Page)
            .Select(i => $"{request.Term}-{i}")
            .ToArray();

        return new SearchResults(request.Term, request.Page, hits);
    }

    #endregion
}
