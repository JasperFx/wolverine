using Wolverine.Http;

namespace IncidentService;

public static class GetIncidentEndpoint
{
    [WolverineGet("/api/incidents/{id}")]
    public static async Task Get(Guid id)
    {
        throw new NotImplementedException();
    }
}