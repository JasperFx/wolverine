using Helpdesk.Api.Incidents;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;
using Wolverine.Http.Marten;

namespace IncidentService;

public record CategoriseIncident(
    Guid IncidentId,
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
            
            // All good, keep going!
            : WolverineContinue.NoProblems;
    }
    
    [WolverinePost("/api/incidents/{incidentId:guid}/category")]
    public static IncidentCategorised Post(
        CategoriseIncident command, 
        [Aggregate("incidentId")] Incident incident, DateTimeOffset now)
    {
        return new IncidentCategorised(command.IncidentId, command.Category, command.CategorisedBy);
    }
}