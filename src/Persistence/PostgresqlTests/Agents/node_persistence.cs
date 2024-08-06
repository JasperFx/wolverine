using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using TestingSupport;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Runtime.Agents;

namespace PostgresqlTests.Agents;

public class node_persistence : PostgresqlContext, IAsyncLifetime
{
    private PostgresqlMessageStore _database;

    public async Task InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();

            await conn.DropSchemaAsync("nodes");

            await conn.CloseAsync();
        }

        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.PostgresConnectionString,
            SchemaName = "nodes",
            IsMaster = true
        };

        _database = new PostgresqlMessageStore(settings, new DurabilitySettings(), NpgsqlDataSource.Create(Servers.PostgresConnectionString),
            NullLogger<PostgresqlMessageStore>.Instance);

        await _database.Admin.MigrateAsync();
    }

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
            Id = id,
            ControlUri = new Uri($"dbcontrol://{id}"),
            Description = Environment.MachineName,

        };

        node.Capabilities.Add(new Uri("red://"));
        node.Capabilities.Add(new Uri("blue://"));
        node.Capabilities.Add(new Uri("green://"));

        var assignedId = await _database.Nodes.PersistAsync(node, CancellationToken.None);

        var nodes = await _database.Nodes.LoadAllNodesAsync(CancellationToken.None);
        var persisted = nodes.Single();
        persisted.Id.ShouldBe(id);
        persisted.AssignedNodeId.ShouldBe(assignedId);
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
            Id = id,
            ControlUri = new Uri($"dbcontrol://{id}"),
            Description = Environment.MachineName,

        };

        node.Capabilities.Add(new Uri("red://"));
        node.Capabilities.Add(new Uri("blue://"));
        node.Capabilities.Add(new Uri("green://"));


        var assignedId = await _database.Nodes.PersistAsync(node, CancellationToken.None);

        // Adding capabilities to prove that the cascade works
        var agent1 = new Uri("red://leader");
        var agent2 = new Uri("red://five");
        var agent3 = new Uri("blue://leader");

        await _database.Nodes.AssignAgentsAsync(id, new[] { agent1, agent2, agent3 }, CancellationToken.None);

        await _database.Nodes.DeleteAsync(id);

        var nodes = await _database.Nodes.LoadAllNodesAsync(CancellationToken.None);
        nodes.ShouldBeEmpty();
    }

    [Fact]
    public async Task write_assignments()
    {
        var id = Guid.NewGuid();
        var node = new WolverineNode
        {
            Id = id,
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
        persisted.ActiveAgents.ShouldHaveTheSameElementsAs(agent1, agent2, agent3);
    }

    [Fact]
    public async Task add_assignments_one_at_a_time()
    {
        var id = Guid.NewGuid();
        var node = new WolverineNode
        {
            Id = id,
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

        var persisted = await _database.Nodes.LoadNodeAsync(node.Id, CancellationToken.None);

        persisted.ActiveAgents.ShouldHaveTheSameElementsAs(agent1, agent2, agent3);
    }

    [Fact]
    public async Task remove_an_assignment()
    {
        var id = Guid.NewGuid();
        var node = new WolverineNode
        {
            Id = id,
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
        persisted.ActiveAgents.ShouldHaveTheSameElementsAs(agent2, agent3);
    }

    private int _count = 0;
    private WolverineNode createNode()
    {
        var id = Guid.NewGuid();
        return new WolverineNode
        {
            Id = id,
            ControlUri = $"dbcontrol://{id}".ToUri(),
            AssignedNodeId = ++_count
        };
    }

    [Fact]
    public async Task mark_leadership_with_no_current_leader()
    {
        var node1 = createNode();

        var assignedId = await _database.Nodes.PersistAsync(node1, CancellationToken.None);

        await _database.Nodes.MarkNodeAsLeaderAsync(null, node1.Id);

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

        await _database.Nodes.MarkNodeAsLeaderAsync(null, node1.Id);

        var assigned = await _database.Nodes.MarkNodeAsLeaderAsync(node1.Id, node3.Id);
        assigned.ShouldBe(node3.Id);

        var nodes = await _database.Nodes.LoadAllNodesAsync(CancellationToken.None);
        var persistedNode3 = nodes.Single(x => x.Id == node3.Id);
        persistedNode3.IsLeader().ShouldBeTrue();

        var persistedNode1 = nodes.Single(x => x.Id == node1.Id);
        persistedNode1.IsLeader().ShouldBeFalse();
    }

    [Fact]
    public async Task mark_leadership_sad_path_with_existing_leader()
    {
        var node1 = createNode();
        var node2 = createNode();
        var node3 = createNode();

        await _database.Nodes.PersistAsync(node1, CancellationToken.None);
        await _database.Nodes.PersistAsync(node2, CancellationToken.None);
        await _database.Nodes.PersistAsync(node3, CancellationToken.None);

        await _database.Nodes.MarkNodeAsLeaderAsync(null, node1.Id);
        await _database.Nodes.MarkNodeAsLeaderAsync(node1.Id, node2.Id);

        // Nope, stays with node2
        var assigned = await _database.Nodes.MarkNodeAsLeaderAsync(node1.Id, node3.Id);
        assigned.ShouldBe(node2.Id);

        var nodes = await _database.Nodes.LoadAllNodesAsync(CancellationToken.None);
        var persistedNode2 = nodes.Single(x => x.Id == node2.Id);
        persistedNode2.IsLeader().ShouldBeTrue();

        var persistedNode3 = nodes.Single(x => x.Id == node3.Id);
        persistedNode3.IsLeader().ShouldBeFalse();

        var persistedNode1 = nodes.Single(x => x.Id == node1.Id);
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
        await _database.Nodes.PersistAsync(node3, CancellationToken.None);

        await _database.Nodes.MarkHealthCheckAsync(node1.Id);
    }

}