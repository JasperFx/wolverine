using Microsoft.Extensions.Logging;
using NSubstitute;
using Wolverine.Persistence;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
///     GH-3954. A node started with <c>Durability.DurabilityAgentEnabled = false</c> never registers the
///     durability agent family, so it throws <c>ArgumentOutOfRangeException: Unrecognized agent scheme
///     'wolverinedb'</c> the moment the leader hands it one. The leader then re-issued the identical
///     assignment every five minutes forever, no durability agent ran anywhere for that store, and
///     <c>owner_id = 0</c> outgoing envelopes were never recovered — silently, with every queue table
///     reading zero.
/// </summary>
/// <remarks>
///     <para>
///         The capability here is per-NODE, not per-agent. The blue/green and group-affinity paths gate on
///         <see cref="AssignmentGrid.AllNodesHaveSameCapabilities(string)" />, which returns true trivially
///         when there is one node AND when every node is equally incapable — precisely the two configurations
///         in the report — so a fix modelled on those paths would have changed neither. The single-node case
///         below is the reporter's own deterministic repro.
///     </para>
/// </remarks>
public class durability_agents_only_go_to_capable_nodes
{
    private static Uri Durability(string db) => new($"wolverinedb://postgresql/localhost/{db}/wolverine");

    private static AssignmentGrid.Node Capable(AssignmentGrid.Node node)
    {
        node.HasCapabilities([MessageStoreCollection.DurabilityCapabilityUri]);
        return node;
    }

    private static void distribute(AssignmentGrid grid)
    {
        grid.DistributeEvenlyWithAffinity("wolverinedb", _ => null,
            MessageStoreCollection.NodeCanRunDurabilityAgents);
    }

    [Fact]
    public void a_lone_incapable_node_is_not_given_the_durability_agent()
    {
        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid());
        grid.WithAgents(Durability("main"));

        distribute(grid);

        // Before this, the _nodes.Count == 1 fast path assigned unconditionally and the node threw
        grid.AgentFor(Durability("main")).AssignedNode.ShouldBeNull();
    }

    [Fact]
    public void a_lone_capable_node_still_takes_everything()
    {
        var grid = new AssignmentGrid();
        Capable(grid.WithNode(1, Guid.NewGuid()));
        grid.WithAgents(Durability("main"));

        distribute(grid);

        grid.AgentFor(Durability("main")).AssignedNode.ShouldNotBeNull();
    }

    [Fact]
    public void the_capable_node_is_chosen_even_when_it_is_the_leader()
    {
        var grid = new AssignmentGrid();
        var leader = Capable(grid.WithNode(1, Guid.NewGuid()));
        leader.IsLeader = true;
        grid.WithNode(2, Guid.NewGuid()); // the producer -- DurabilityAgentEnabled = false
        grid.WithAgents(Durability("main"));

        distribute(grid);

        // The remainder pass prefers !IsLeader, which in this two node cluster deterministically targeted
        // the incapable producer -- the exact shape reported
        grid.AgentFor(Durability("main")).AssignedNode.ShouldBe(leader);
    }

    [Fact]
    public void an_agent_already_parked_on_an_incapable_node_is_moved_off()
    {
        var grid = new AssignmentGrid();
        var capable = Capable(grid.WithNode(1, Guid.NewGuid()));
        var incapable = grid.WithNode(2, Guid.NewGuid());
        grid.WithAgents(Durability("main"));

        incapable.Assign(grid.AgentFor(Durability("main")));

        distribute(grid);

        grid.AgentFor(Durability("main")).AssignedNode.ShouldBe(capable);
    }

    [Fact]
    public void nothing_is_assigned_when_no_node_is_capable()
    {
        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid());
        grid.WithNode(2, Guid.NewGuid());
        grid.WithAgents(Durability("one"), Durability("two"));

        distribute(grid);

        grid.AgentFor(Durability("one")).AssignedNode.ShouldBeNull();
        grid.AgentFor(Durability("two")).AssignedNode.ShouldBeNull();
    }

    [Fact]
    public void capable_nodes_still_get_an_even_spread()
    {
        var grid = new AssignmentGrid();
        Capable(grid.WithNode(1, Guid.NewGuid()));
        Capable(grid.WithNode(2, Guid.NewGuid()));
        grid.WithAgents(Durability("one"), Durability("two"), Durability("three"), Durability("four"));

        distribute(grid);

        foreach (var node in grid.Nodes)
        {
            node.Agents.Count.ShouldBe(2);
        }
    }

    /// <summary>
    ///     The GH-3785 affinity preference follows another family's placements, and that family may be running
    ///     on a node that cannot host durability agents. Co-location is an optimisation; capability is not.
    /// </summary>
    [Fact]
    public void an_affinity_preference_for_an_incapable_node_is_discarded()
    {
        var grid = new AssignmentGrid();
        var capable = Capable(grid.WithNode(1, Guid.NewGuid()));
        var incapable = grid.WithNode(2, Guid.NewGuid());
        grid.WithAgents(Durability("main"));

        grid.DistributeEvenlyWithAffinity("wolverinedb", _ => incapable,
            MessageStoreCollection.NodeCanRunDurabilityAgents);

        grid.AgentFor(Durability("main")).AssignedNode.ShouldBe(capable);
    }

    [Fact]
    public void warns_once_when_no_node_in_the_cluster_can_run_durability_agents()
    {
        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid());
        grid.WithAgents(Durability("main"));

        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);

        var warned = false;
        MessageStoreCollection.WarnIfNoCapableNode(grid, logger, ref warned);
        warned.ShouldBeTrue();

        MessageStoreCollection.WarnIfNoCapableNode(grid, logger, ref warned);

        logger.ReceivedWithAnyArgs(1).Log(default, default, default!, default, default!);
    }

    [Fact]
    public void does_not_warn_when_at_least_one_node_is_capable()
    {
        var grid = new AssignmentGrid();
        Capable(grid.WithNode(1, Guid.NewGuid()));
        grid.WithAgents(Durability("main"));

        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);

        var warned = false;
        MessageStoreCollection.WarnIfNoCapableNode(grid, logger, ref warned);

        warned.ShouldBeFalse();
        logger.DidNotReceiveWithAnyArgs().Log(default, default, default!, default, default!);
    }
}
