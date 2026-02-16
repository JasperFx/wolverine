using Marten;
using Wolverine.Attributes;
using Wolverine.Http;

namespace WolverineWebApi;

public static class NonTransactionalEndpoint
{
    [NonTransactional]
    [WolverinePost("/non-transactional")]
    public static string Post(IDocumentSession session)
    {
        return "not transactional";
    }
}
