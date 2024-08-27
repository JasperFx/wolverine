using Raven.Embedded;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.RavenDb.Internals;

namespace RavenDbTests;

[Collection("raven")]
public class node_persistence_compliance : NodePersistenceCompliance
{
    private readonly DatabaseFixture _fixture;

    public node_persistence_compliance(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    protected override async Task<IMessageStore> buildCleanMessageStore()
    {
        var store = _fixture.StartRavenStore();
        return new RavenDbMessageStore(store);
    }
}