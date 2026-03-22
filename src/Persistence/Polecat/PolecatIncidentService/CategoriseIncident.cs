using Microsoft.AspNetCore.Mvc;
using Polecat;
using Wolverine.Http;

namespace PolecatIncidentService;

#region sample_polecat_CategoriseIncident

public record CategoriseIncident(
    IncidentCategory Category,
    Guid CategorisedBy,
    int Version
);

public static class CategoriseIncidentEndpoint
{
    // NOTE: There is no Wolverine.Http.Polecat equivalent of [Aggregate] yet.
    // For now, we load the aggregate manually via IDocumentSession.
    // This is a known gap — see the missing features report.
    [WolverinePost("/api/incidents/{incidentId:guid}/category")]
    public static async Task<IResult> Post(
        Guid incidentId,
        CategoriseIncident command,
        IDocumentSession session,
        CancellationToken token)
    {
        var stream = await session.Events.FetchForWriting<Incident>(incidentId, command.Version, token);
        var incident = stream.Aggregate;

        if (incident == null)
            return Results.NotFound();

        if (incident.Status == IncidentStatus.Closed)
            return Results.Problem(detail: "Incident is already closed");

        stream.AppendOne(new IncidentCategorised(incident.Id, command.Category, command.CategorisedBy));
        await session.SaveChangesAsync(token);

        return Results.NoContent();
    }
}

#endregion
