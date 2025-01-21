using Helpdesk.Api.Incidents;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;
using Wolverine.Http.Marten;

namespace IncidentService;

#region sample_CategoriseIncident

public record CategoriseIncident(
    IncidentCategory Category,
    Guid CategorisedBy,
    int Version
);

public static class CategoriseIncidentEndpoint
{
    // This is Wolverine's form of "Railway Programming"
    // Wolverine will execute this before the main endpoint,
    // and stop all processing if the ProblemDetails is *not*
    // "NoProblems"
    public static ProblemDetails Validate(Incident incident)
    {
        return incident.Status == IncidentStatus.Closed 
            ? new ProblemDetails { Detail = "Incident is already closed" } 
            
            // All good, keep going!
            : WolverineContinue.NoProblems;
    }
    
    // This tells Wolverine that the first "return value" is NOT the response
    // body
    [EmptyResponse]
    [WolverinePost("/api/incidents/{incidentId:guid}/category")]
    public static IncidentCategorised Post(
        // the actual command
        CategoriseIncident command, 
        
        // Wolverine is generating code to look up the Incident aggregate
        // data for the event stream with this id
        [Aggregate("incidentId")] Incident incident)
    {
        // This is a simple case where we're just appending a single event to
        // the stream.
        return new IncidentCategorised(incident.Id, command.Category, command.CategorisedBy);
    }
}

#endregion