using Wolverine.Persistence.EventSourcing;

namespace Wolverine.Polecat;

// GH-3907: the Polecat half of the shared aggregate handler workflow's store seam. Deliberately a
// sibling of IPolecatPersistenceFrameProvider's contract rather than more members on it, so stores
// with no event sourcing never grow no-op aggregate members. Everything store-specific the shared
// workflow needs is reachable through here.
internal class PolecatEventSourcingFrameProvider : IEventSourcingFrameProvider
{
    public string StoreName => "Polecat";

    // Wolverine.Polecat.Events stays public and store-side - GH-3907 retires nothing in this release.
    public Type EventsCollectionType => typeof(Events);
}
