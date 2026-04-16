using Polecat;
using Wolverine.Http;

namespace PolecatIncidentService;

public static class GetIncidentEndpoint
{
    [WolverineGet("/api/incidents/{id}")]
    public static async Task<Incident?> Get(
        Guid id,
        IDocumentSession session,
        CancellationToken token)
    {
        return await session.Events.FetchLatest<Incident>(id, token);
    }
}
