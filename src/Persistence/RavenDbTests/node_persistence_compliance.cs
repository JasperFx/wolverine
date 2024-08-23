using Raven.Embedded;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.RavenDb.Internals;

namespace RavenDbTests;

[Collection("raven")]
public class node_persistence_compliance : NodePersistenceCompliance
{
    public node_persistence_compliance(DatabaseFixture fixture)
    {
    }

    protected override async Task<IMessageStore> buildCleanMessageStore()
    {
        var store = await EmbeddedServer.Instance.GetDocumentStoreAsync(Guid.NewGuid().ToString());
        return new RavenDbMessageStore(store);
    }
}