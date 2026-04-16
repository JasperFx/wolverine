using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;
using Wolverine.Http.Polecat;

namespace PolecatIncidentService;

#region sample_polecat_CategoriseIncident

public record CategoriseIncident(
    IncidentCategory Category,
    Guid CategorisedBy,
    int Version
);

public static class CategoriseIncidentEndpoint
{
    public static ProblemDetails Validate(Incident incident)
    {
        return incident.Status == IncidentStatus.Closed
            ? new ProblemDetails { Detail = "Incident is already closed" }
            : WolverineContinue.NoProblems;
    }

    [EmptyResponse]
    [WolverinePost("/api/incidents/{incidentId:guid}/category")]
    public static IncidentCategorised Post(
        CategoriseIncident command,
        [Aggregate("incidentId")] Incident incident)
    {
        return new IncidentCategorised(incident.Id, command.Category, command.CategorisedBy);
    }
}

#endregion
