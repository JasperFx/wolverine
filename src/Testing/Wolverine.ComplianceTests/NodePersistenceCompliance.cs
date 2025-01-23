using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Agents;
using Xunit;

namespace Wolverine.ComplianceTests;

public abstract class NodePersistenceCompliance : IAsyncLifetime
{
    private IMessageStore _database;

    public async Task InitializeAsync()
    {
        _database = await buildCleanMessageStore();
    }

    protected abstract Task<IMessageStore> buildCleanMessageStore();

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task returns_no_nodes_on_blank_slate()
    {
        var nodes = await _database.Nodes.LoadAllNodesAsync(CancellationToken.None);
        nodes.ShouldBeEmpty();
    }

    [Fact]
    public async Task persist_single_node()
    {
        var id = Guid.NewGuid();
        var node = new WolverineNode
        {
            NodeId = id,
            ControlUri = new Uri($"dbcontrol://{id}"),
            Description = Environment.MachineName,

        };

        node.Capabilities.Add(new Uri("red://"));
        node.Capabilities.Add(new Uri("blue://"));
        node.Capabilities.Add(new Uri("green://"));

        var assignedId = await _database.Nodes.PersistAsync(node, CancellationToken.None);

        var nodes = await _database.Nodes.LoadAllNodesAsync(CancellationToken.None);
        var persisted = nodes.Single();
        persisted.NodeId.ShouldBe(id);
        persisted.AssignedNodeNumber.ShouldBe(assignedId);
        persisted.ControlUri.ShouldBe(node.ControlUri);
        persisted.Description.ShouldBe(node.Description);

        persisted.Capabilities.ShouldHaveTheSameElementsAs(node.Capabilities.ToArray());
    }

    [Fact]
    public async Task delete_a_single_node()
    {
        var id = Guid.NewGuid();
        var node = new WolverineNode
        {
            NodeId = id,
            ControlUri = new Uri($"dbcontrol://{id}"),
            Description = Environment.MachineName,

        };

        node.Capabilities.Add(new Uri("red://"));
        node.Capabilities.Add(new Uri("blue://"));
        node.Capabilities.Add(new Uri("green://"));


        var assignedNodeNumber = await _database.Nodes.PersistAsync(node, CancellationToken.None);

        // Adding capabilities to prove that the cascade works
        var agent1 = new Uri("red://leader");
        var agent2 = new Uri("red://five");
        var agent3 = new Uri("blue://leader");

        await _database.Nodes.AssignAgentsAsync(id, new[] { agent1, agent2, agent3 }, CancellationToken.None);

        await _database.Nodes.DeleteAsync(id, assignedNodeNumber);

        var nodes = await _database.Nodes.LoadAllNodesAsync(CancellationToken.None);
        nodes.ShouldBeEmpty();
    }

    [Fact]
    public async Task write_assignments()
    {
        var id = Guid.NewGuid();
        var node = new WolverineNode
        {
            NodeId = id,
            ControlUri = new Uri($"dbcontrol://{id}"),
            Description = Environment.MachineName,

        };

        node.Capabilities.Add(new Uri("red://"));
        node.Capabilities.Add(new Uri("blue://"));
        node.Capabilities.Add(new Uri("green://"));

        var assignedId = await _database.Nodes.PersistAsync(node, CancellationToken.None);

        var agent1 = new Uri("red://leader");
        var agent2 = new Uri("red://five");
        var agent3 = new Uri("blue://leader");

        await _database.Nodes.AssignAgentsAsync(id, new[] { agent1, agent2, agent3 }, CancellationToken.None);

        var nodes = await _database.Nodes.LoadAllNodesAsync(CancellationToken.None);
        var persisted = nodes.Single();
        persisted.ActiveAgents.OrderBy(x => x.ToString()).ShouldBe([agent3, agent2, agent1]);
    }

    [Fact]
    public async Task add_assignments_one_at_a_time()
    {
        var id = Guid.NewGuid();
        var node = new WolverineNode
        {
            NodeId = id,
            ControlUri = new Uri($"dbcontrol://{id}"),
            Description = Environment.MachineName,

        };

        node.Capabilities.Add(new Uri("red://"));
        node.Capabilities.Add(new Uri("blue://"));
        node.Capabilities.Add(new Uri("green://"));

        var assignedId = await _database.Nodes.PersistAsync(node, CancellationToken.None);

        var agent1 = new Uri("red://leader");
        var agent2 = new Uri("red://five");
        var agent3 = new Uri("blue://leader");

        await _database.Nodes.AddAssignmentAsync(id, agent1, CancellationToken.None);
        await _database.Nodes.AddAssignmentAsync(id, agent2, CancellationToken.None);
        await _database.Nodes.AddAssignmentAsync(id, agent3, CancellationToken.None);

        var persisted = await _database.Nodes.LoadNodeAsync(node.NodeId, CancellationToken.None);

        persisted.ActiveAgents.OrderBy(x => x.ToString()).ShouldBe([agent3, agent2, agent1]);
    }

    [Fact]
    public async Task remove_an_assignment()
    {
        var id = Guid.NewGuid();
        var node = new WolverineNode
        {
            NodeId = id,
            ControlUri = new Uri($"dbcontrol://{id}"),
            Description = Environment.MachineName,

        };

        node.Capabilities.Add(new Uri("red://"));
        node.Capabilities.Add(new Uri("blue://"));
        node.Capabilities.Add(new Uri("green://"));

        var assignedId = await _database.Nodes.PersistAsync(node, CancellationToken.None);

        var agent1 = new Uri("red://leader");
        var agent2 = new Uri("red://five");
        var agent3 = new Uri("blue://leader");

        await _database.Nodes.AddAssignmentAsync(id, agent1, CancellationToken.None);
        await _database.Nodes.AddAssignmentAsync(id, agent2, CancellationToken.None);
        await _database.Nodes.AddAssignmentAsync(id, agent3, CancellationToken.None);

        // Now remove 1
        await _database.Nodes.RemoveAssignmentAsync(id, agent1, CancellationToken.None);

        var nodes = await _database.Nodes.LoadAllNodesAsync(CancellationToken.None);
        var persisted = nodes.Single();
        persisted.ActiveAgents.OrderBy(x => x.ToString()).ShouldBe([agent3, agent2]);
    }

    private int _count = 0;
    private WolverineNode createNode()
    {
        var id = Guid.NewGuid();
        return new WolverineNode
        {
            NodeId = id,
            ControlUri = $"dbcontrol://{id}".ToUri(),
            AssignedNodeNumber = ++_count
        };
    }

    [Fact]
    public async Task mark_leadership_with_no_current_leader()
    {
        var node1 = createNode();

        var assignedId = await _database.Nodes.PersistAsync(node1, CancellationToken.None);

        await _database.Nodes.MarkNodeAsLeaderAsync(null, node1.NodeId);

        var nodes = await _database.Nodes.LoadAllNodesAsync(CancellationToken.None);
        var persisted = nodes.Single();

        persisted.IsLeader().ShouldBeTrue();
    }

    [Fact]
    public async Task mark_leadership_happy_path_with_existing_leader()
    {
        var node1 = createNode();
        var node2 = createNode();
        var node3 = createNode();

        await _database.Nodes.PersistAsync(node1, CancellationToken.None);
        await _database.Nodes.PersistAsync(node2, CancellationToken.None);
        await _database.Nodes.PersistAsync(node3, CancellationToken.None);

        await _database.Nodes.MarkNodeAsLeaderAsync(null, node1.NodeId);

        var assigned = await _database.Nodes.MarkNodeAsLeaderAsync(node1.NodeId, node3.NodeId);
        assigned.ShouldBe(node3.NodeId);

        var nodes = await _database.Nodes.LoadAllNodesAsync(CancellationToken.None);
        var persistedNode3 = nodes.Single(x => x.NodeId == node3.NodeId);
        persistedNode3.IsLeader().ShouldBeTrue();

        var persistedNode1 = nodes.Single(x => x.NodeId == node1.NodeId);
        persistedNode1.IsLeader().ShouldBeFalse();
    }
    
    [Fact]
    public async Task update_health_check_smoke_test()
    {
        var node1 = createNode();
        var node2 = createNode();
        var node3 = createNode();

        await _database.Nodes.PersistAsync(node1, CancellationToken.None);
        await _database.Nodes.PersistAsync(node2, CancellationToken.None);
        //await _database.Nodes.PersistAsync(node3, CancellationToken.None);

        await _database.Nodes.MarkHealthCheckAsync(node1, CancellationToken.None);
        await _database.Nodes.MarkHealthCheckAsync(node2, CancellationToken.None);
        await _database.Nodes.MarkHealthCheckAsync(node3, CancellationToken.None);

        // Proving the upsert behavior
        var nodes = await _database.Nodes.LoadAllNodesAsync(CancellationToken.None);
        nodes.Any(x => x.NodeId == node3.NodeId).ShouldBeTrue();
    }

}