using Raven.Embedded;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Agents;
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
        return new RavenDbMessageStore(store, new WolverineOptions());
    }

    [Fact]
    public async Task concurrently_persisting_nodes_assigns_unique_node_numbers()
    {
        await using var messageStore = await buildCleanMessageStore();

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nodes = Enumerable.Range(0, 20).Select(_ =>
        {
            var id = Guid.NewGuid();
            return new WolverineNode
            {
                NodeId = id,
                ControlUri = new Uri($"dbcontrol://{id}"),
                Description = Environment.MachineName,
                Version = new Version(1, 2, 3, 0)
            };
        }).ToArray();

        var tasks = nodes.Select(async node =>
        {
            await start.Task;
            return await messageStore.Nodes.PersistAsync(node, CancellationToken.None);
        }).ToArray();

        start.SetResult();

        var assignedNodeNumbers = await Task.WhenAll(tasks);

        assignedNodeNumbers.OrderBy(x => x).ShouldBe(Enumerable.Range(1, nodes.Length).ToArray());

        var persisted = await messageStore.Nodes.LoadAllNodesAsync(CancellationToken.None);
        persisted.Select(x => x.AssignedNodeNumber).OrderBy(x => x).ShouldBe(assignedNodeNumbers.OrderBy(x => x));
    }
}
