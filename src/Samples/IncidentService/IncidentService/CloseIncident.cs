using JasperFx.Core;
using Wolverine;
using Wolverine.Http;
using Wolverine.Http.Marten;
using Wolverine.Marten;

namespace IncidentService;

public record CloseIncident(
    Guid ClosedBy,
    int Version
);

public static class CloseIncidentHandler
{
    [WolverinePost("/api/incidents/close/{id}")]
    public static (UpdatedAggregate, Events, OutgoingMessages) Handle(
        CloseIncident command, 
        
        [Aggregate]
        Incident incident)
    {
        /* More logic for later
        if (current.Status is not IncidentStatus.ResolutionAcknowledgedByCustomer)
               throw new InvalidOperationException("Only incident with acknowledged resolution can be closed");

           if (current.HasOutstandingResponseToCustomer)
               throw new InvalidOperationException("Cannot close incident that has outstanding responses to customer");

         */
        
        
        if (incident.Status == IncidentStatus.Closed)
        {
            return (new UpdatedAggregate(), [], []);
        }

        return (

            // Returning the latest view of
            // the Incident as the actual response body
            new UpdatedAggregate(),

            // New event to be appended to the Incident stream
            [new IncidentClosed(command.ClosedBy)],

            // Getting fancy here, telling Wolverine to schedule a 
            // command message for three days from now
            [new ArchiveIncident(incident.Id).DelayedFor(3.Days())]);
    }
}