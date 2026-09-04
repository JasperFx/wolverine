using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

public class capacity_aware_distribution
{
    private readonly Uri blue1 = new Uri("blue://1");
    private readonly Uri blue2 = new Uri("blue://2");
    private readonly Uri blue3 = new Uri("blue://3");
    private readonly Uri blue4 = new Uri("blue://4");
    private readonly Uri blue5 = new Uri("blue://5");
    private readonly Uri blue6 = new Uri("blue://6");

    [Fact]
    public void shed_pass_never_detaches_pinned_agents()
    {
        var grid = new AssignmentGrid();
        grid.OverloadShedBatchSize = 2;

        var node1 = grid.WithNode(1, Guid.NewGuid()).Running(blue1, blue2, blue3);
        var node2 = grid.WithNode(2, Guid.NewGuid());

        node1.IsOverloaded = true;
        node1.IsAcceptingAgents = false;

        grid.AgentFor(blue1).IsPinned = true;
        grid.AgentFor(blue2).IsPinned = true;

        grid.DistributeEvenly("blue");

        // Only the unpinned agent may be shed; the pins stay put
        grid.AgentFor(blue1).AssignedNode.ShouldBe(node1);
        grid.AgentFor(blue2).AssignedNode.ShouldBe(node1);
        grid.AgentFor(blue3).AssignedNode.ShouldBe(node2);
        grid.AgentFor(blue3).SheddedForCapacity.ShouldBeTrue();
    }

    [Fact]
    public void ceiling_detach_never_detaches_pinned_agents()
    {
        var grid = new AssignmentGrid();

        // 6 agents over 2 nodes -> ceiling of 3, so node1 must give up 2 of its 5.
        // With 3 of them pinned, the 2 detached must both be unpinned.
        var node1 = grid.WithNode(1, Guid.NewGuid()).Running(blue1, blue2, blue3, blue4, blue5);
        var node2 = grid.WithNode(2, Guid.NewGuid()).Running(blue6);

        grid.AgentFor(blue1).IsPinned = true;
        grid.AgentFor(blue2).IsPinned = true;
        grid.AgentFor(blue3).IsPinned = true;

        grid.DistributeEvenly("blue");

        grid.AgentFor(blue1).AssignedNode.ShouldBe(node1);
        grid.AgentFor(blue2).AssignedNode.ShouldBe(node1);
        grid.AgentFor(blue3).AssignedNode.ShouldBe(node1);

        // The unpinned extras moved to the other node and total load stayed even
        node1.Agents.Count.ShouldBe(3);
        node2.Agents.Count.ShouldBe(3);
    }

    [Fact]
    public void overloaded_node_receives_no_placements()
    {
        var grid = new AssignmentGrid();

        var node1 = grid.WithNode(1, Guid.NewGuid());
        var node2 = grid.WithNode(2, Guid.NewGuid());

        node1.IsOverloaded = true;
        node1.IsAcceptingAgents = false;

        grid.WithAgents(blue1, blue2, blue3, blue4);
        grid.DistributeEvenly("blue");

        node1.Agents.ShouldBeEmpty();
        node2.Agents.Count.ShouldBe(4);
    }

    [Fact]
    public void all_nodes_overloaded_leaves_agents_waiting()
    {
        var grid = new AssignmentGrid();

        var node1 = grid.WithNode(1, Guid.NewGuid()).Running(blue1);
        var node2 = grid.WithNode(2, Guid.NewGuid()).Running(blue2);

        foreach (var node in grid.Nodes)
        {
            node.IsOverloaded = true;
            node.IsAcceptingAgents = false;
        }

        grid.WithAgents(blue3, blue4);
        grid.DistributeEvenly("blue");

        // Nothing new is placed anywhere; the unassigned agents wait
        grid.AgentFor(blue3).AssignedNode.ShouldBeNull();
        grid.AgentFor(blue4).AssignedNode.ShouldBeNull();
    }

    [Fact]
    public void node_in_the_hysteresis_band_neither_sheds_nor_receives()
    {
        var grid = new AssignmentGrid();

        // Between the receive line and the shed line: not overloaded, not accepting
        var node1 = grid.WithNode(1, Guid.NewGuid()).Running(blue1, blue2);
        var node2 = grid.WithNode(2, Guid.NewGuid());

        node1.IsAcceptingAgents = false;

        grid.WithAgents(blue3, blue4);
        grid.DistributeEvenly("blue");

        // Keeps what it has, takes nothing new
        grid.AgentFor(blue1).AssignedNode.ShouldBe(node1);
        grid.AgentFor(blue2).AssignedNode.ShouldBe(node1);
        grid.AgentFor(blue3).AssignedNode.ShouldBe(node2);
        grid.AgentFor(blue4).AssignedNode.ShouldBe(node2);
    }

    [Fact]
    public void memory_pressure_monitor_honors_the_0_to_100_contract()
    {
        var monitor = new MemoryPressureLoadMonitor();

        var load = monitor.CurrentLoad();

        if (load.HasValue)
        {
            load.Value.ShouldBeGreaterThanOrEqualTo(0);
            load.Value.ShouldBeLessThanOrEqualTo(100);
        }
    }
}
