using Polecat;

namespace PolecatTests.AncillaryStores;

// Marker interfaces for ancillary Polecat stores, mirroring MartenTests.AncillaryStores.
public interface IPlayerStore : IDocumentStore;

public interface IThingStore : IDocumentStore;

public class Player
{
    public string Id { get; set; } = null!;
}

public class Thing
{
    public string Id { get; set; } = null!;
}
