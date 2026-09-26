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

        // GH-4590: and the agents that WERE running are still running. Shedding is only ever a move,
        // so with nowhere to move to there is nothing to be gained by stopping them.
        grid.AgentFor(blue1).AssignedNode.ShouldBe(node1);
        grid.AgentFor(blue2).AssignedNode.ShouldBe(node2);
    }

    /// <summary>
    /// GH-4590. The failure this covers only appears across CONSECUTIVE evaluations: each one shed
    /// OverloadShedBatchSize agents and placed none of them, so a scheme drained to nothing one batch
    /// at a time. A single-tick assertion passed throughout.
    /// </summary>
    [Fact]
    public void an_overloaded_cluster_does_not_drain_itself_over_successive_evaluations()
    {
        var node1Id = Guid.NewGuid();
        var node2Id = Guid.NewGuid();

        var on1 = new List<Uri> { blue1, blue2, blue3 };
        var on2 = new List<Uri> { blue4, blue5, blue6 };

        for (var tick = 0; tick < 5; tick++)
        {
            var grid = new AssignmentGrid();

            var node1 = grid.WithNode(1, node1Id).Running(on1.ToArray());
            var node2 = grid.WithNode(2, node2Id).Running(on2.ToArray());

            foreach (var node in grid.Nodes)
            {
                node.IsOverloaded = true;
                node.IsAcceptingAgents = false;
            }

            grid.DistributeEvenly("blue");

            on1 = node1.Agents.Select(x => x.Uri).ToList();
            on2 = node2.Agents.Select(x => x.Uri).ToList();
        }

        on1.Count.ShouldBe(3);
        on2.Count.ShouldBe(3);
    }

    /// <summary>
    /// GH-4590, the unconditional case: a single-node cluster has nowhere to shed to by definition,
    /// so an overloaded node used to stop its own agents one per evaluation and never restart them.
    /// </summary>
    [Fact]
    public void a_single_overloaded_node_keeps_running_what_it_has()
    {
        var nodeId = Guid.NewGuid();
        var running = new List<Uri> { blue1, blue2, blue3 };

        for (var tick = 0; tick < 5; tick++)
        {
            var grid = new AssignmentGrid();

            var node = grid.WithNode(1, nodeId).Running(running.ToArray());
            node.IsOverloaded = true;
            node.IsAcceptingAgents = false;

            grid.DistributeEvenly("blue");

            running = node.Agents.Select(x => x.Uri).ToList();
        }

        running.Count.ShouldBe(3);
    }

    /// <summary>
    /// GH-4590. A node advertising nothing is still ELIGIBLE — that is what keeps stores with no load
    /// persistence on today's behavior — but it must not outrank a node that has actually reported
    /// itself lightly loaded. Otherwise a node mid-rolling-upgrade, or one whose sampler is throwing,
    /// becomes the preferred target for every placement precisely when least is known about it.
    /// </summary>
    [Fact]
    public void a_node_advertising_no_load_does_not_outrank_a_measurably_idle_one()
    {
        var grid = new AssignmentGrid();

        var quiet = grid.WithNode(1, Guid.NewGuid());
        quiet.LoadFactor = 5;

        var unknown = grid.WithNode(2, Guid.NewGuid());
        unknown.LoadFactor = null;

        grid.WithAgents(blue1);
        grid.DistributeEvenly("blue");

        grid.AgentFor(blue1).AssignedNode.ShouldBe(quiet);
    }

    [Fact]
    public void a_node_advertising_no_load_still_beats_a_measurably_busy_one()
    {
        var grid = new AssignmentGrid();

        var busy = grid.WithNode(1, Guid.NewGuid());
        busy.LoadFactor = 75;

        var unknown = grid.WithNode(2, Guid.NewGuid());
        unknown.LoadFactor = null;

        grid.WithAgents(blue1);
        grid.DistributeEvenly("blue");

        grid.AgentFor(blue1).AssignedNode.ShouldBe(unknown);
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
    public void loads_within_the_same_band_defer_to_the_foreign_count_ordering()
    {
        var grid = new AssignmentGrid();

        // node1 reads marginally lighter, but both readings sit in the same 10-point band, so the
        // GH-3877 foreign-count order still decides — the node carrying other schemes' work loses.
        var node1 = grid.WithNode(1, Guid.NewGuid()).Running(new Uri("red://1"), new Uri("red://2"));
        node1.LoadFactor = 41.2;
        var node2 = grid.WithNode(2, Guid.NewGuid());
        node2.LoadFactor = 44.7;

        grid.WithAgents(blue1);
        grid.DistributeEvenly("blue");

        grid.AgentFor(blue1).AssignedNode.ShouldBe(node2);
    }

    [Fact]
    public void loads_in_different_bands_outrank_the_foreign_count_ordering()
    {
        var grid = new AssignmentGrid();

        // A band's worth of separation means the advertised load wins even against a node
        // carrying nothing foreign.
        var node1 = grid.WithNode(1, Guid.NewGuid()).Running(new Uri("red://1"), new Uri("red://2"));
        node1.LoadFactor = 15;
        var node2 = grid.WithNode(2, Guid.NewGuid());
        node2.LoadFactor = 45;

        grid.WithAgents(blue1);
        grid.DistributeEvenly("blue");

        grid.AgentFor(blue1).AssignedNode.ShouldBe(node1);
    }
}
