using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.CosmosDb.Internals;
using Wolverine.Persistence.Durability;

namespace CosmosDbTests;

[Collection("cosmosdb")]
public class node_persistence_compliance : NodePersistenceCompliance
{
    private readonly AppFixture _fixture;

    public node_persistence_compliance(AppFixture fixture)
    {
        _fixture = fixture;
    }

    protected override async Task<IMessageStore> buildCleanMessageStore()
    {
        await _fixture.ClearAll();
        return _fixture.BuildMessageStore();
    }
}
