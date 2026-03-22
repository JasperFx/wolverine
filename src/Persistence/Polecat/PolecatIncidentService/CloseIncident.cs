using JasperFx.Core;
using Polecat;
using Wolverine;
using Wolverine.Http;

namespace PolecatIncidentService;

public record CloseIncident(
    Guid ClosedBy,
    int Version
);

public static class CloseIncidentEndpoint
{
    // NOTE: No [Aggregate] attribute available for Polecat HTTP endpoints yet.
    // Using manual aggregate loading via IDocumentSession.
    [WolverinePost("/api/incidents/close/{id}")]
    public static async Task<IResult> Handle(
        Guid id,
        CloseIncident command,
        IDocumentSession session,
        IMessageBus bus,
        CancellationToken token)
    {
        var stream = await session.Events.FetchForWriting<Incident>(id, command.Version, token);
        var incident = stream.Aggregate;

        if (incident == null)
            return Results.NotFound();

        if (incident.Status == IncidentStatus.Closed)
        {
            // Idempotent — already closed
            var current = await session.Events.FetchLatest<Incident>(id, token);
            return Results.Ok(current);
        }

        stream.AppendOne(new IncidentClosed(command.ClosedBy));
        await session.SaveChangesAsync(token);

        // Schedule the archive command for 3 days from now
        await bus.PublishAsync(new ArchiveIncident(id), new DeliveryOptions
        {
            ScheduleDelay = 3.Days()
        });

        var updated = await session.Events.FetchLatest<Incident>(id, token);
        return Results.Ok(updated);
    }
}
