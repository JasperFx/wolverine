using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.CosmosDb.Internals;
using Shouldly;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Agents;

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

    /// <summary>
    /// GH-4593. Unlike RavenDB, this store maps through a hand-written DTO rather than persisting the
    /// WolverineNode whole, so LoadFactor had to be added to CosmosWolverineNode and to both halves of its
    /// mapping. MarkHealthCheckAsync is the path that matters: it edits the document it just read, so
    /// without setting the field there the advertisement would be written at registration and then never
    /// refreshed again.
    /// </summary>
    [Fact]
    public async Task load_factor_round_trips_through_the_heartbeat_write()
    {
        await using var messageStore = await buildCleanMessageStore();

        var id = Guid.NewGuid();
        var node = new WolverineNode
        {
            NodeId = id,
            ControlUri = new Uri($"dbcontrol://{id}"),
            Description = Environment.MachineName
        };

        node.AssignedNodeNumber = await messageStore.Nodes.PersistAsync(node, CancellationToken.None);

        node.LoadFactor = 42.5;
        (await messageStore.Nodes.MarkHealthCheckAsync(node, CancellationToken.None)).ShouldBeTrue();

        (await messageStore.Nodes.LoadNodeAsync(id, CancellationToken.None))!.LoadFactor.ShouldBe(42.5);

        // a later heartbeat has to MOVE the reading, not just write it once
        node.LoadFactor = 13.25;
        await messageStore.Nodes.MarkHealthCheckAsync(node, CancellationToken.None);

        (await messageStore.Nodes.LoadNodeAsync(id, CancellationToken.None))!.LoadFactor.ShouldBe(13.25);
    }
}
