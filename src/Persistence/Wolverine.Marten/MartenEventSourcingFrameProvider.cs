using Wolverine.Persistence.EventSourcing;

namespace Wolverine.Marten;

// GH-3907: the Marten half of the shared aggregate handler workflow's store seam. Deliberately a
// sibling of IMartenPersistenceFrameProvider's contract rather than more members on it, so stores
// with no event sourcing never grow no-op aggregate members. Everything store-specific the shared
// workflow needs is reachable through here.
internal class MartenEventSourcingFrameProvider : IEventSourcingFrameProvider
{
    public string StoreName => "Marten";

    // Wolverine.Marten.Events stays public and store-side - GH-3907 retires nothing in this release.
    public Type EventsCollectionType => typeof(Events);
}
