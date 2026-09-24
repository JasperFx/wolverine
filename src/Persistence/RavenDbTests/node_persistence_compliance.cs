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

    /// <summary>
    /// GH-4593. A document store carries LoadFactor for free on the paths that write the WolverineNode
    /// whole -- but MarkHealthCheckAsync deliberately patches single properties, and that is the
    /// once-per-heartbeat path. Without a patch for it the advertisement would be written at registration
    /// and then never refreshed again, which reads to the leader as a node frozen at its startup load.
    /// </summary>
    [Fact]
    public async Task load_factor_round_trips_through_the_heartbeat_patch()
    {
        var options = new WolverineOptions();
        options.Durability.CapacityAwareAssignment = true;

        await using var messageStore = new RavenDbMessageStore(_fixture.StartRavenStore(), options);

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
