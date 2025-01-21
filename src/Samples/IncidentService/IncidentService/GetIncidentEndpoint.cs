using Marten;
using Marten.AspNetCore;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace IncidentService;

public static class GetIncidentEndpoint
{
    // For right now, you have to help out the OpenAPI metdata
    [WolverineGet("/api/incidents/{id}")]
    public static async Task<Incident?> Get(
        Guid id, 
        IDocumentSession session, 
        
        // This will be the HttpContext.RequestAborted
        CancellationToken token)
    {
        return await session.Events.FetchLatest<Incident>(id, token);
    }
}