using JasperFx.Core;
using Wolverine;
using Wolverine.Http;
using Wolverine.Http.Polecat;
using Wolverine.Polecat;

namespace PolecatIncidentService;

public record CloseIncident(
    Guid ClosedBy,
    int Version
);

public static class CloseIncidentEndpoint
{
    [WolverinePost("/api/incidents/close/{id}")]
    public static (UpdatedAggregate, Events, OutgoingMessages) Handle(
        CloseIncident command,
        [Aggregate]
        Incident incident)
    {
        if (incident.Status == IncidentStatus.Closed)
        {
            return (new UpdatedAggregate(), [], []);
        }

        return (
            new UpdatedAggregate(),
            [new IncidentClosed(command.ClosedBy)],
            [new ArchiveIncident(incident.Id).DelayedFor(3.Days())]);
    }
}
