using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Persistence;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
///     GH-5163. An agent the leader leaves without a node says why, so a monitor can tell an agent waiting on
///     capacity from one that never started.
/// </summary>
public class unplaced_agents_carry_a_reason_5163
{
    private readonly Uri blue1 = new("blue://1");
    private readonly Uri blue2 = new("blue://2");
    private readonly Uri blue3 = new("blue://3");
    private readonly Uri blue4 = new("blue://4");

    [Fact]
    public void agents_left_waiting_because_no_node_is_accepting_are_awaiting_capacity()
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

        grid.AgentFor(blue3).UnplacedReason.ShouldBe(UnplacedReason.AwaitingCapacity);
        grid.AgentFor(blue4).UnplacedReason.ShouldBe(UnplacedReason.AwaitingCapacity);

        // The agents still running where they were have nothing to explain
        grid.AgentFor(blue1).AssignedNode.ShouldBe(node1);
        grid.AgentFor(blue1).UnplacedReason.ShouldBeNull();
        grid.AgentFor(blue2).AssignedNode.ShouldBe(node2);
        grid.AgentFor(blue2).UnplacedReason.ShouldBeNull();
    }

    [Fact]
    public void the_reason_is_cleared_once_the_agent_is_placed()
    {
        var grid = new AssignmentGrid();
        var node = grid.WithNode(1, Guid.NewGuid());
        node.IsAcceptingAgents = false;

        grid.WithAgents(blue1, blue2);
        grid.DistributeEvenly("blue");
        grid.AgentFor(blue1).UnplacedReason.ShouldBe(UnplacedReason.AwaitingCapacity);

        // The node recovers and the next pass places everything
        node.IsAcceptingAgents = true;
        grid.DistributeEvenly("blue");

        grid.AgentFor(blue1).AssignedNode.ShouldBe(node);
        grid.AgentFor(blue1).UnplacedReason.ShouldBeNull();
        grid.AgentFor(blue2).UnplacedReason.ShouldBeNull();
    }

    [Fact]
    public void an_agent_no_node_declares_has_no_capable_node()
    {
        var grid = new AssignmentGrid();

        // Mixed capabilities, so this takes the blue/green path, and nobody declares blue3
        grid.WithNode(1, Guid.NewGuid()).HasCapabilities([blue1, blue2]);
        grid.WithNode(2, Guid.NewGuid()).HasCapabilities([blue1]);
        grid.WithAgents(blue1, blue2, blue3);

        grid.DistributeEvenlyWithBlueGreenSemantics("blue");

        grid.AgentFor(blue1).UnplacedReason.ShouldBeNull();
        grid.AgentFor(blue2).UnplacedReason.ShouldBeNull();
        grid.AgentFor(blue3).AssignedNode.ShouldBeNull();
        grid.AgentFor(blue3).UnplacedReason.ShouldBe(UnplacedReason.NoCapableNode);
    }

    [Fact]
    public void agents_with_no_capable_node_under_affinity_distribution_say_so()
    {
        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid()).Running(blue1);
        grid.WithNode(2, Guid.NewGuid());
        grid.WithAgents(blue2);

        grid.DistributeEvenlyWithAffinity("blue", _ => null, _ => false);

        grid.AgentFor(blue1).AssignedNode.ShouldBeNull();
        grid.AgentFor(blue1).UnplacedReason.ShouldBe(UnplacedReason.NoCapableNode);
        grid.AgentFor(blue2).UnplacedReason.ShouldBe(UnplacedReason.NoCapableNode);
    }

    [Fact]
    public void a_paused_agent_is_paused()
    {
        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid()).Running(blue1, blue2);

        var restrictions = new AgentRestrictions([]);
        restrictions.PauseAgent(blue1);
        grid.ApplyRestrictions(restrictions);

        grid.DistributeEvenly("blue");

        grid.AgentFor(blue1).AssignedNode.ShouldBeNull();
        grid.AgentFor(blue1).UnplacedReason.ShouldBe(UnplacedReason.Paused);
        grid.AgentFor(blue2).UnplacedReason.ShouldBeNull();
    }

    [Fact]
    public void the_leader_logs_agents_awaiting_capacity_once_per_change()
    {
        var grid = new AssignmentGrid();
        var node = grid.WithNode(1, Guid.NewGuid());
        node.IsAcceptingAgents = false;
        grid.WithAgents(blue1, blue2);
        grid.DistributeEvenly("blue");

        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(LogLevel.Information).Returns(true);

        var reported = 0;
        NodeAgentController.ReportAgentsAwaitingCapacity(grid, logger, ref reported);
        reported.ShouldBe(2);

        // The same parked steady state on the next evaluation says nothing new
        NodeAgentController.ReportAgentsAwaitingCapacity(grid, logger, ref reported);
        logger.ReceivedWithAnyArgs(1).Log(default, default, default!, default, default!);

        // Capacity comes back, so the next one that parks agents is news again
        node.IsAcceptingAgents = true;
        grid.DistributeEvenly("blue");
        NodeAgentController.ReportAgentsAwaitingCapacity(grid, logger, ref reported);
        reported.ShouldBe(0);
        logger.ReceivedWithAnyArgs(1).Log(default, default, default!, default, default!);
    }

    [Fact]
    public async Task the_leader_marks_agents_parked_when_every_node_is_over_the_accept_threshold()
    {
        await using var cluster = new SimulatedCluster(nodeCount: 2, agentCount: 6, seed: 5163);
        cluster.Options.Durability.CapacityAwareAssignment = true;
        cluster.Options.Durability.NodeOverloadThreshold = 90;

        // Below the overload line, so nothing sheds, but above a custom accept line, so nothing is received
        cluster.Options.Durability.NodeAcceptThreshold = 50;
        foreach (var nodeId in cluster.NodeIds) cluster.AdvertiseLoad(nodeId, 60);

        await cluster.RunRoundAsync();

        var unplaced = cluster.LastAssignments!.UnassignedAgents;
        unplaced.ShouldNotBeEmpty();
        unplaced.ShouldAllBe(x => x.UnplacedReason == UnplacedReason.AwaitingCapacity);
        cluster.RunningAgents.ShouldBeEmpty();

        // Back under the accept line: everything is placed and nothing is left with a reason
        foreach (var nodeId in cluster.NodeIds) cluster.AdvertiseLoad(nodeId, 40);
        await cluster.RunUntilConvergedAsync(20);

        cluster.LastAssignments!.UnassignedAgents.ShouldBeEmpty();
        cluster.LastAssignments.AllAgents.ShouldAllBe(x => x.UnplacedReason == null);
    }

    [Fact]
    public async Task agents_held_back_by_a_new_leader_are_takeover_holds()
    {
        // A cold cluster's first node with a wave larger than one start batch holds first-time placement back
        // for LeaderColdStartHoldEvaluations evaluations, so its peers can register
        await using var cluster = new SimulatedCluster(nodeCount: 1, agentCount: 20, seed: 5163);
        cluster.Options.Durability.AgentStartBatchSize = 5;
        cluster.Options.Durability.LeaderColdStartHoldEvaluations = 1;

        await cluster.RunRoundAsync();

        var held = cluster.LastAssignments!.UnassignedAgents.Where(x => x.Uri.Scheme == "fake").ToArray();
        held.ShouldNotBeEmpty();
        held.ShouldAllBe(x => x.UnplacedReason == UnplacedReason.TakeoverHold);
    }

    // GH-5163 follow-up: the paths CritterWatch pointed at, and the ones the invariant check in SimulatedCluster
    // turned up. Every agent a full evaluation leaves without a node says why.

    private static AssignmentGrid.Node warmupNode(AssignmentGrid grid, int id, params Uri[] capabilities) =>
        grid.WithNode(id, Guid.NewGuid()).HasCapabilities([..capabilities, NodeAgentController.WarmupRoleUri]);

    [Fact]
    public void evenly_distributed_agents_on_a_cluster_of_only_warmup_nodes_have_no_capable_node()
    {
        // Warm-up nodes never take messaging work, whatever their load, so this is not a capacity problem
        var grid = new AssignmentGrid();
        warmupNode(grid, 1, blue1, blue2);
        warmupNode(grid, 2, blue1, blue2);
        grid.WithAgents(blue1, blue2);

        grid.DistributeEvenly("blue");

        grid.UnassignedAgents.Count.ShouldBe(2);
        grid.UnassignedAgents.ShouldAllBe(x => x.UnplacedReason == UnplacedReason.NoCapableNode);
    }

    [Fact]
    public void serving_nodes_that_are_all_full_still_mean_awaiting_capacity_beside_a_warmup_node()
    {
        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid()).IsAcceptingAgents = false;
        warmupNode(grid, 2, blue1);
        grid.WithAgents(blue1);

        grid.DistributeEvenly("blue");

        grid.AgentFor(blue1).UnplacedReason.ShouldBe(UnplacedReason.AwaitingCapacity);
    }

    [Fact]
    public async Task a_singular_agent_on_a_cluster_of_only_warmup_nodes_has_no_capable_node()
    {
        IAgentFamily family = new SimpleSingularAgent();
        var uri = new Uri("simple://");

        var grid = new AssignmentGrid();
        warmupNode(grid, 1, uri);
        grid.WithAgents(uri);

        await family.EvaluateAssignmentsAsync(grid);

        grid.AgentFor(uri).AssignedNode.ShouldBeNull();
        grid.AgentFor(uri).UnplacedReason.ShouldBe(UnplacedReason.NoCapableNode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void group_affinity_on_a_cluster_of_only_warmup_nodes_places_what_they_declare(bool sameCapabilities)
    {
        // The case CritterWatch asked about. Warm-up nodes in the grid always force the capability-matching path,
        // so the candidates ARE computed: what the warm-up nodes declare goes to them, and what nobody declares
        // is NoCapableNode, never null
        var green1 = new Uri("blue://db1/green1");
        var green2 = new Uri("blue://db1/green2");
        var nobody = new Uri("blue://db2/nobody");

        var grid = new AssignmentGrid();
        warmupNode(grid, 1, green1, green2);
        warmupNode(grid, 2, sameCapabilities ? [green1, green2] : [green1]);
        grid.WithAgents(green1, green2, nobody);

        grid.DistributeByGroupAffinity("blue", uri => uri.Host);

        grid.AgentFor(green1).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(green2).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(nobody).AssignedNode.ShouldBeNull();
        grid.AgentFor(nobody).UnplacedReason.ShouldBe(UnplacedReason.NoCapableNode);
    }

    [Fact]
    public void blue_green_on_a_cluster_of_only_warmup_nodes_says_why_for_what_it_cannot_place()
    {
        var grid = new AssignmentGrid();
        warmupNode(grid, 1, blue1, blue2);
        warmupNode(grid, 2, blue1);
        grid.WithAgents(blue1, blue2, blue3);

        grid.DistributeEvenlyWithBlueGreenSemantics("blue");

        grid.AgentFor(blue1).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue2).AssignedNode.ShouldNotBeNull();
        grid.AgentFor(blue3).UnplacedReason.ShouldBe(UnplacedReason.NoCapableNode);
    }

    [Fact]
    public async Task a_retired_event_subscription_agent_is_superseded()
    {
        var identity = new EventStoreIdentity("main", "marten");
        var database = new DatabaseId("localhost", "claims1");
        var shard = ShardName.Compose("provided-cares");

        var store = Substitute.For<IEventStore>();
        store.Identity.Returns(identity);
        store.DatabaseCardinality.Returns(DatabaseCardinality.StaticMultiple);
        store.DistributesAgentsPerTenant.Returns(true);
        store.StubUsage(new EventStoreUsage
        {
            Database = new DatabaseUsage
            {
                Databases =
                {
                    new DatabaseDescriptor
                    {
                        ServerName = database.Server, DatabaseName = database.Name, TenantIds = ["t1"]
                    }
                }
            },
            Subscriptions =
            {
                new SubscriptionDescriptor(SubscriptionType.SingleStreamProjection)
                {
                    Lifecycle = ProjectionLifecycle.Async, ShardNames = [shard]
                }
            }
        });

        var family = new EventSubscriptionAgentFamily([store], []);

        // The store-global agent from before the database had a tenant is still running, and its node still
        // declares it from its startup capabilities
        var storeGlobal = EventSubscriptionAgentFamily.UriFor(identity, database, shard);
        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid()).Running(storeGlobal);
        grid.WithAgents(EventSubscriptionAgentFamily.UriFor(identity, database, shard.ForTenant("t1")));

        await family.EvaluateAssignmentsAsync(grid);

        // Stopped on purpose, which is not the same thing as waiting for a node
        grid.AgentFor(storeGlobal).AssignedNode.ShouldBeNull();
        grid.AgentFor(storeGlobal).UnplacedReason.ShouldBe(UnplacedReason.Superseded);
    }

    [Fact]
    public async Task node_markers_are_not_agents_in_the_leaders_grid()
    {
        // Every serving node with durability agents publishes wolverine://durability-agents-enabled, and every
        // warm-up node wolverine://node-role/warmup. Both used to be seeded into the grid as agents that were
        // never assigned and never had a reason. SimulatedCluster checks the invariant after every round.
        var family = new FakeAgentFamily("fake", 6);
        await using var cluster = new SimulatedCluster(nodeCount: 2, family, seed: 5163,
            capabilitiesFor: _ => [..family.AllAgentUris(), MessageStoreCollection.DurabilityCapabilityUri]);
        cluster.AddNode([..family.AllAgentUris(), NodeAgentController.WarmupRoleUri]);

        // The simulation counts every declared capability as an agent to converge on, markers included, so run
        // a fixed number of rounds rather than to convergence
        for (var i = 0; i < 5; i++) await cluster.RunRoundAsync();

        cluster.LastAssignments!.AllAgents.ShouldNotContain(x => NodeAgentController.IsNodeMarker(x.Uri));
        cluster.LastAssignments.UnassignedAgents.ShouldBeEmpty();

        // The markers still do their job: the warm-up node got none of the messaging agents
        cluster.LastAssignments.Nodes.Single(x => x.IsWarmup).Agents.ShouldBeEmpty();
    }
}
