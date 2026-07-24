using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Agents;
using Xunit;

namespace Wolverine.ComplianceTests;

public abstract class NodePersistenceCompliance : IAsyncLifetime
{
    private IMessageStore _database = null!;

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
            Version = new Version(1,2,3,0)
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
        persisted.Version.ShouldBe(new Version(1,2,3,0));

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

        persisted!.ActiveAgents.OrderBy(x => x.ToString()).ShouldBe([agent3, agent2, agent1]);
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
            AssignedNodeNumber = ++_count,
            Version = new Version(2, 0, 1)
        };
    }

    [Fact]
    public async Task delete_old_node_records_does_not_throw_on_empty_table()
    {
        // Should not throw even with no records
        await _database.Nodes.DeleteOldNodeRecordsAsync(5);
    }

    [Fact]
    public async Task delete_old_node_records_with_zero_retain_does_not_throw()
    {
        // retainCount <= 0 should be a safe no-op
        await _database.Nodes.DeleteOldNodeRecordsAsync(0);
    }

    [Fact]
    public async Task mark_health_check_reports_whether_the_node_row_exists()
    {
        var node1 = createNode();
        var node2 = createNode();
        var missing = createNode();

        await _database.Nodes.PersistAsync(node1, CancellationToken.None);
        await _database.Nodes.PersistAsync(node2, CancellationToken.None);
        // 'missing' is deliberately NOT persisted.

        // GH-3604 / D2: MarkHealthCheckAsync returns true for a live row and must NOT blindly insert a
        // skeleton for a missing one -- it returns false so the controller can re-register with real identity.
        (await _database.Nodes.MarkHealthCheckAsync(node1, CancellationToken.None)).ShouldBeTrue();
        (await _database.Nodes.MarkHealthCheckAsync(node2, CancellationToken.None)).ShouldBeTrue();
        (await _database.Nodes.MarkHealthCheckAsync(missing, CancellationToken.None)).ShouldBeFalse();

        var nodes = await _database.Nodes.LoadAllNodesAsync(CancellationToken.None);
        nodes.Count.ShouldBe(2);
        nodes.Any(x => x.NodeId == missing.NodeId).ShouldBeFalse();
    }

    [Fact]
    public async Task reregister_after_ejection_preserves_identity_and_restores_assignments()
    {
        // Regression coverage for GH-3604 / D2. A peer deletes a still-live node's row (and, via the
        // assignment FK cascade, its assignment rows). The node's next heartbeat must be able to resurrect
        // itself with the SAME node number, the SAME capabilities, and its agent assignments restored --
        // instead of a fresh skeleton with a new number and no capabilities (which drops the node out of
        // capability-matched distribution). This exercises the persistence primitives the controller uses.
        var id = Guid.NewGuid();
        var node = new WolverineNode
        {
            NodeId = id,
            ControlUri = new Uri($"dbcontrol://{id}"),
            Description = Environment.MachineName,
            Version = new Version(4, 5, 6, 0)
        };
        node.Capabilities.Add(new Uri("red://"));
        node.Capabilities.Add(new Uri("blue://"));
        node.Capabilities.Add(new Uri("green://"));

        var assignedNumber = await _database.Nodes.PersistAsync(node, CancellationToken.None);
        node.AssignedNodeNumber = assignedNumber;

        var agent1 = new Uri("red://leader");
        var agent2 = new Uri("blue://five");
        await _database.Nodes.AssignAgentsAsync(id, new[] { agent1, agent2 }, CancellationToken.None);

        // A peer ejects the still-live node: delete the row + its assignments.
        await _database.Nodes.DeleteAsync(id, assignedNumber);
        (await _database.Nodes.LoadAllNodesAsync(CancellationToken.None)).ShouldBeEmpty();

        // The node's next heartbeat sees the row is gone (false) and re-registers with its real identity.
        (await _database.Nodes.MarkHealthCheckAsync(node, CancellationToken.None)).ShouldBeFalse();
        await _database.Nodes.ReregisterNodeAsync(node, CancellationToken.None);
        await _database.Nodes.AssignAgentsAsync(id, new[] { agent1, agent2 }, CancellationToken.None);

        var nodes = await _database.Nodes.LoadAllNodesAsync(CancellationToken.None);
        var resurrected = nodes.Single();
        resurrected.NodeId.ShouldBe(id);
        resurrected.AssignedNodeNumber.ShouldBe(assignedNumber);
        resurrected.Capabilities.OrderBy(x => x.ToString())
            .ShouldBe([new Uri("blue://"), new Uri("green://"), new Uri("red://")]);
        resurrected.ActiveAgents.OrderBy(x => x.ToString()).ShouldBe([agent2, agent1]);
    }

}