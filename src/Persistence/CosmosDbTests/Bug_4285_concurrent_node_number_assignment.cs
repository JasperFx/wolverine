using Shouldly;
using Wolverine.Runtime.Agents;

namespace CosmosDbTests;

// GH-4285: two nodes starting at the same moment both read the node sequence document at the
// same count and both wrote count + 1 back, so both were assigned the same AssignedNodeNumber —
// and because envelope ownership is released by node number on shutdown, one node's exit
// released the other still-running node's in-flight envelopes for redelivery.
[Collection("cosmosdb")]
public class Bug_4285_concurrent_node_number_assignment
{
    private readonly AppFixture _fixture;

    public Bug_4285_concurrent_node_number_assignment(AppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task concurrently_persisting_nodes_assigns_unique_node_numbers()
    {
        await _fixture.ClearAll();
        var messageStore = _fixture.BuildMessageStore();

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
        persisted.Select(x => x.AssignedNodeNumber).OrderBy(x => x)
            .ShouldBe(assignedNodeNumbers.OrderBy(x => x));
    }
}
