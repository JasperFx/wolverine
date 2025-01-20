using Marten;

namespace IncidentService;

public record ArchiveIncident(Guid IncidentId);

public static class ArchiveIncidentHandler
{
    // Just going to code this one pretty crudely
    public static void Handle(ArchiveIncident command, IDocumentSession session)
    {
        // TODO -- do more here w/ Jeffry
    }
}