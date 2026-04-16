using JasperFx.Events;
using Polecat;

namespace PolecatIncidentService;

public record ArchiveIncident(Guid IncidentId);

public static class ArchiveIncidentHandler
{
    // Just going to code this one pretty crudely
    public static void Handle(ArchiveIncident command, IDocumentSession session)
    {
        session.Events.Append(command.IncidentId, new Archived("It's done baby!"));
        session.Delete<Incident>(command.IncidentId);
    }
}
