using Wolverine.ComplianceTests;
using Wolverine.Persistence;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
///     GH-4569. A <see cref="Wolverine.NodeRole.Warmup" /> node joins the serving cluster to warm up the new release's
///     projection versions before it is cut over to. It publishes <see cref="NodeAgentController.WarmupRoleUri" /> in
///     its capabilities, and the leader then keeps everything off it except the event subscription agents that no
///     serving node declares. Before the role existed, blue vs green was inferred from capabilities alone, so green
///     took a share of the shared, un-bumped projections (and rebalanced them again when it left), durability and
///     recurring work, and exclusive listeners.
/// </summary>
public class warmup_node_role_assignment_4569
{
    private const string Scheme = "event-subscriptions";

    private static Uri projection(string name, uint version, string db = "main") =>
        new($"{Scheme}://marten/main/{db}/{name}/All/v{version}");

    private static readonly Uri[] Shared = new[] { "invoices", "claim-lines", "returns", "timeline", "payments", "notes" }
        .Select(x => projection(x, 3))
        .ToArray();

    private static readonly Uri BlueOnly = projection("provided-cares", 33);
    private static readonly Uri GreenOnly = projection("provided-cares", 34);

    private static AssignmentGrid.Node serving(AssignmentGrid grid, int id, params Uri[] capabilities) =>
        grid.WithNode(id, Guid.NewGuid()).HasCapabilities(capabilities);

    private static AssignmentGrid.Node warmup(AssignmentGrid grid, int id, params Uri[] capabilities) =>
        grid.WithNode(id, Guid.NewGuid()).HasCapabilities(capabilities.Append(NodeAgentController.WarmupRoleUri));

    private static AssignmentGrid blueGreenGrid(out AssignmentGrid.Node[] blue, out AssignmentGrid.Node green)
    {
        var grid = new AssignmentGrid();
        blue =
        [
            serving(grid, 1, [..Shared, BlueOnly]),
            serving(grid, 2, [..Shared, BlueOnly])
        ];
        blue[0].IsLeader = true;

        green = warmup(grid, 3, [..Shared, GreenOnly]);

        grid.WithAgents([..Shared, BlueOnly, GreenOnly]);
        return grid;
    }

    [Fact]
    public void a_node_publishing_the_marker_is_a_warmup_node()
    {
        var grid = new AssignmentGrid();
        serving(grid, 1, Shared).IsWarmup.ShouldBeFalse();
        warmup(grid, 2, Shared).IsWarmup.ShouldBeTrue();
    }

    [Fact]
    public void the_warmup_node_takes_only_what_no_serving_node_declares()
    {
        var grid = blueGreenGrid(out var blue, out var green);

        grid.DistributeEvenlyWithBlueGreenSemantics(Scheme);

        grid.AgentFor(GreenOnly).AssignedNode.ShouldBe(green);

        foreach (var shared in Shared)
        {
            blue.ShouldContain(grid.AgentFor(shared).AssignedNode!);
        }

        blue.ShouldContain(grid.AgentFor(BlueOnly).AssignedNode!);
        green.Agents.ShouldHaveSingleItem();
    }

    [Fact]
    public void a_shared_agent_already_on_the_warmup_node_is_moved_to_a_serving_node()
    {
        var grid = blueGreenGrid(out var blue, out var green);

        // Placed there by a leader that predates node roles
        green.Running(Shared[0], Shared[1], GreenOnly);

        grid.DistributeEvenlyWithBlueGreenSemantics(Scheme);

        blue.ShouldContain(grid.AgentFor(Shared[0]).AssignedNode!);
        blue.ShouldContain(grid.AgentFor(Shared[1]).AssignedNode!);
        grid.AgentFor(GreenOnly).AssignedNode.ShouldBe(green);
    }

    [Fact]
    public void the_serving_nodes_keep_a_balanced_share_of_their_own_agents()
    {
        var grid = blueGreenGrid(out var blue, out _);

        grid.DistributeEvenlyWithBlueGreenSemantics(Scheme);

        // Seven agents the serving nodes can run, split over the two serving nodes -- not a ceiling computed over
        // all three nodes, which would push shared agents back and forth between the blue nodes
        blue[0].Agents.Count.ShouldBeInRange(3, 4);
        blue[1].Agents.Count.ShouldBeInRange(3, 4);
    }

    [Fact]
    public void a_steady_grid_with_a_warmup_node_issues_no_further_commands()
    {
        var first = blueGreenGrid(out _, out _);
        first.DistributeEvenlyWithBlueGreenSemantics(Scheme);
        var placed = first.CompileAssignments();

        // The next evaluation sees every agent running where the first one put it
        var second = new AssignmentGrid();
        var b1 = serving(second, 1, [..Shared, BlueOnly]);
        b1.IsLeader = true;
        var b2 = serving(second, 2, [..Shared, BlueOnly]);
        var g = warmup(second, 3, [..Shared, GreenOnly]);
        second.WithAgents([..Shared, BlueOnly, GreenOnly]);

        var byId = new Dictionary<int, AssignmentGrid.Node> { [1] = b1, [2] = b2, [3] = g };
        foreach (var pair in placed)
        {
            var original = first.Nodes.Single(x => x.NodeId == pair.Value.NodeId);
            byId[original.AssignedId].Running(pair.Key);
        }

        second.DistributeEvenlyWithBlueGreenSemantics(Scheme);

        second.AllAgents.Where(x => x.AssignedNode != x.OriginalNode).ShouldBeEmpty();
    }

    // A grid with only warm-up nodes in it is never evaluated: a warm-up node never contends for leadership, so
    // with every serving node gone there is no leader, and green keeps exactly what it was running. What the grid
    // has to get right is a single surviving serving node.
    [Fact]
    public void a_lone_surviving_serving_node_takes_every_shared_agent_back_from_the_warmup_node()
    {
        var grid = new AssignmentGrid();
        var blue = serving(grid, 1, [..Shared, BlueOnly]);
        blue.IsLeader = true;
        var green = warmup(grid, 3, [..Shared, GreenOnly]);
        grid.WithAgents([..Shared, BlueOnly, GreenOnly]);

        green.Running(Shared);

        grid.DistributeEvenlyWithBlueGreenSemantics(Scheme);

        grid.AgentFor(GreenOnly).AssignedNode.ShouldBe(green);
        foreach (var shared in Shared)
        {
            grid.AgentFor(shared).AssignedNode.ShouldBe(blue);
        }
    }

    [Fact]
    public void group_affinity_keeps_the_shared_partitions_on_the_serving_nodes()
    {
        var grid = new AssignmentGrid();
        Uri[] dbAgents(string db, uint bumped) =>
            [projection("invoices", 3, db), projection("timeline", 9, db), projection("provided-cares", bumped, db)];

        var databases = Enumerable.Range(1, 6).Select(i => $"shard_{i:D3}").ToArray();
        var blueCapabilities = databases.SelectMany(db => dbAgents(db, 33)).ToArray();
        var greenCapabilities = databases.SelectMany(db => dbAgents(db, 34)).ToArray();

        var blue1 = serving(grid, 1, blueCapabilities);
        blue1.IsLeader = true;
        var blue2 = serving(grid, 2, blueCapabilities);
        var green = warmup(grid, 3, greenCapabilities);

        grid.WithAgents(blueCapabilities.Concat(greenCapabilities).Distinct().ToArray());

        grid.DistributeByGroupAffinity(Scheme, uri => uri.Segments[2]);

        foreach (var agent in grid.AllAgents)
        {
            if (agent.Uri.ToString().Contains("/v34"))
            {
                agent.AssignedNode.ShouldBe(green);
            }
            else
            {
                new[] { blue1, blue2 }.ShouldContain(agent.AssignedNode!);
            }
        }
    }

    [Fact]
    public void an_evenly_distributed_agent_never_lands_on_a_warmup_node()
    {
        var grid = new AssignmentGrid();
        var leader = serving(grid, 1);
        leader.IsLeader = true;
        var green = warmup(grid, 2);

        var listeners = Enumerable.Range(1, 4).Select(i => new Uri($"wolverine-listener://rabbitmq/queue/q{i}")).ToArray();
        grid.WithAgents(listeners);

        // A leader that predates node roles left one on the warm-up node
        green.Running(listeners[0]);

        grid.DistributeEvenly("wolverine-listener");

        foreach (var listener in listeners)
        {
            grid.AgentFor(listener).AssignedNode.ShouldBe(leader);
        }
    }

    [Fact]
    public async Task a_singular_agent_skips_the_warmup_node_even_though_it_is_not_the_leader()
    {
        var grid = new AssignmentGrid();
        var leader = serving(grid, 1);
        leader.IsLeader = true;
        warmup(grid, 2);

        IAgentFamily singular = new SimpleSingularAgent();
        var uri = new Uri("simple://");
        grid.WithAgents(uri);

        await singular.EvaluateAssignmentsAsync(grid);

        // A singular agent prefers a non-leader node, and a warm-up node is never the leader
        grid.AgentFor(uri).AssignedNode.ShouldBe(leader);
    }

    [Fact]
    public void durability_agents_never_go_to_a_warmup_node_even_with_a_permissive_predicate()
    {
        var grid = new AssignmentGrid();
        var leader = serving(grid, 1, MessageStoreCollection.DurabilityCapabilityUri);
        leader.IsLeader = true;
        var green = warmup(grid, 2);

        var durability = new Uri("wolverinedb://postgresql/localhost/main/wolverine");
        grid.WithAgents(durability);
        green.Running(durability);

        grid.DistributeEvenlyWithAffinity("wolverinedb", _ => null, _ => true);

        grid.AgentFor(durability).AssignedNode.ShouldBe(leader);
    }
}
