using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-4592, the other two capability-constrained paths. Same governing rule as group affinity: node load
/// orders the candidates but never removes one, because the candidate set is already a hard capability
/// constraint and emptying it strands the agent rather than delaying it.
/// </summary>
public class capacity_aware_capability_paths
{
    private readonly Uri blue1 = new("blue://1");
    private readonly Uri blue2 = new("blue://2");
    private readonly Uri blue3 = new("blue://3");
    private readonly Uri blue4 = new("blue://4");

    [Fact]
    public void blue_green_prefers_a_capable_node_with_headroom()
    {
        var grid = new AssignmentGrid();

        // Both nodes can run blue1; only node1 can run blue3, which keeps capabilities heterogeneous so
        // the blue/green path is taken rather than delegating to DistributeEvenly.
        var busy = grid.WithNode(1, Guid.NewGuid()).HasCapabilities(new[] { blue1, blue2, blue3 });
        var idle = grid.WithNode(2, Guid.NewGuid()).HasCapabilities(new[] { blue1, blue2 });

        busy.LoadFactor = 95;
        busy.IsOverloaded = true;
        busy.IsAcceptingAgents = false;
        idle.LoadFactor = 5;

        grid.WithAgents(blue1, blue2, blue3);
        grid.DistributeEvenlyWithBlueGreenSemantics("blue");

        grid.AgentFor(blue1).AssignedNode.ShouldBe(idle);
        grid.AgentFor(blue2).AssignedNode.ShouldBe(idle);

        // blue3 has exactly one capable node. An overloaded node still beats no node: a version's agent
        // not running at all mid-rollout is worse than it running somewhere busy.
        grid.AgentFor(blue3).AssignedNode.ShouldBe(busy);
    }

    [Fact]
    public void blue_green_still_places_when_every_capable_node_is_overloaded()
    {
        var grid = new AssignmentGrid();

        grid.WithNode(1, Guid.NewGuid()).HasCapabilities(new[] { blue1, blue2, blue3 });
        grid.WithNode(2, Guid.NewGuid()).HasCapabilities(new[] { blue1, blue2 });

        foreach (var node in grid.Nodes)
        {
            node.IsOverloaded = true;
            node.IsAcceptingAgents = false;
            node.LoadFactor = 98;
        }

        grid.WithAgents(blue1, blue2, blue3);
        grid.DistributeEvenlyWithBlueGreenSemantics("blue");

        grid.AgentFor(blue1).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue2).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue3).AssignedNode.ShouldNotBeNull();
    }

    /// <summary>
    /// Capacity decides the ORDER the capable nodes fill in, not how many each may hold: the even
    /// ceiling still applies, so two agents over two nodes is still one apiece however loaded either is.
    /// What load changes is who gets the odd one out — here, the only one.
    /// </summary>
    [Fact]
    public void the_affinity_remainder_fills_the_node_with_headroom_first()
    {
        var grid = new AssignmentGrid();

        var busy = grid.WithNode(1, Guid.NewGuid());
        var idle = grid.WithNode(2, Guid.NewGuid());

        busy.LoadFactor = 95;
        busy.IsOverloaded = true;
        busy.IsAcceptingAgents = false;
        idle.LoadFactor = 5;

        grid.WithAgents(blue1);

        // No preference, so the agent falls into the evenly-spread remainder. Without the capacity
        // ordering the node-id tie-break would hand it to the overloaded node.
        grid.DistributeEvenlyWithAffinity("blue", _ => null, _ => true);

        grid.AgentFor(blue1).AssignedNode.ShouldBe(idle);
    }

    [Fact]
    public void the_even_ceiling_still_bounds_the_affinity_remainder()
    {
        var grid = new AssignmentGrid();

        var busy = grid.WithNode(1, Guid.NewGuid());
        var idle = grid.WithNode(2, Guid.NewGuid());

        busy.LoadFactor = 95;
        busy.IsOverloaded = true;
        busy.IsAcceptingAgents = false;
        idle.LoadFactor = 5;

        grid.WithAgents(blue1, blue2);
        grid.DistributeEvenlyWithAffinity("blue", _ => null, _ => true);

        // One each. Concentrating both onto the idle node would trade the memory problem for the
        // connection-pool one GH-3785 exists to avoid.
        grid.Nodes.ShouldAllBe(n => n.Agents.Count == 1);
    }

    /// <summary>
    /// GH-3785's co-location is not second-guessed on load. The preference exists so a shard database
    /// attracts ONE node's connection pool instead of two; overriding it under pressure would reopen
    /// exactly the problem it closed, and the durability agent is cheap next to the projections it is
    /// following.
    /// </summary>
    [Fact]
    public void an_explicit_affinity_preference_outranks_node_load()
    {
        var grid = new AssignmentGrid();

        var busy = grid.WithNode(1, Guid.NewGuid());
        var idle = grid.WithNode(2, Guid.NewGuid());

        busy.LoadFactor = 99;
        busy.IsOverloaded = true;
        busy.IsAcceptingAgents = false;
        idle.LoadFactor = 1;

        grid.WithAgents(blue1, blue2);

        grid.DistributeEvenlyWithAffinity("blue", uri => uri == blue1 ? busy : null, _ => true);

        grid.AgentFor(blue1).AssignedNode.ShouldBe(busy);
        grid.AgentFor(blue2).AssignedNode.ShouldBe(idle);
    }

    [Fact]
    public void the_affinity_remainder_still_places_when_no_capable_node_has_headroom()
    {
        var grid = new AssignmentGrid();

        grid.WithNode(1, Guid.NewGuid());
        grid.WithNode(2, Guid.NewGuid());

        foreach (var node in grid.Nodes)
        {
            node.IsOverloaded = true;
            node.IsAcceptingAgents = false;
            node.LoadFactor = 97;
        }

        grid.WithAgents(blue1, blue2, blue3, blue4);
        grid.DistributeEvenlyWithAffinity("blue", _ => null, _ => true);

        foreach (var uri in new[] { blue1, blue2, blue3, blue4 })
        {
            grid.AgentFor(uri).AssignedNode.ShouldNotBeNull();
        }
    }
}
