using Bobcat;
using Bobcat.Xunit;
using Wolverine.Runtime.Agents;
using static CoreTests.Runtime.Agents.Workloads.WorkloadCluster;

namespace CoreTests.Runtime.Agents.Workloads;

/// <summary>
///     GH-5601. Every node of one Wolverine application runs the same code against the same message store, and
///     <c>Durability.Workloads</c> decides what each node takes on: Messaging (listeners, the listener agents,
///     scheduled messages, recurring schedules), Durability (the <c>wolverinedb://</c> agents) and Projections (the
///     <c>event-subscriptions://</c> agents). A node with none of them is an HTTP-only node. These are the leader's
///     decisions about who runs what. The vocabulary is <see cref="WorkloadCluster" />.
/// </summary>
[BobcatFeature("Node workloads decide which node runs which agent")]
public class node_workloads_decide_agent_placement_5601
{
    private readonly WorkloadCluster the = new();

    [BobcatSpec]
    public async Task an_http_only_node_runs_no_agents()
    {
        the.Nodes("1 (HTTP only), 2 (Messaging, Projections), 3 (All)");
        the.OneAgentOfEveryFamily();

        await the.TheLeaderEvaluates();

        the.NodeRunsNothing(1);
        the.NodeNeverLeads(1);
    }

    [BobcatSpec]
    public async Task every_agent_family_runs_only_on_nodes_running_its_workload()
    {
        the.Nodes("1 (HTTP only), 2 (Messaging, Projections), 3 (All)");
        the.NodeLeads(2);
        the.OneAgentOfEveryFamily();

        await the.TheLeaderEvaluates();

        // Durability: only node 3 runs it
        the.AgentRunsOn(Durability, 3);

        // Projections: nodes 2 and 3
        the.AgentRunsOnEither(Projection, 2, 3);

        // Messaging: nodes 2 and 3, the leader-pinned listener on the leader
        the.AgentRunsOnEither(ExclusiveListener, 2, 3);
        the.AgentRunsOnEither(DynamicListener, 2, 3);
        the.AgentRunsOnEither(RecurringTimer, 2, 3);
        the.AgentRunsOnEither(RecurringSchedules, 2, 3);
        the.AgentRunsOnEither(CustomAgent, 2, 3);
        the.AgentRunsOn(LeaderPinnedListener, 2);

        the.NodeRunsNothing(1);
    }

    [BobcatSpec]
    public async Task projections_go_only_to_nodes_running_projections()
    {
        the.Nodes("1 (Messaging, Durability), 2 (Messaging, Projections)");
        the.OneAgentOfEveryFamily();

        await the.TheLeaderEvaluates();

        the.AgentRunsOn(Projection, 2);
        the.AgentRunsOn(Durability, 1);
    }

    [BobcatSpec]
    public async Task a_projections_only_node_runs_projections_and_nothing_else()
    {
        the.Nodes("1 (Messaging, Durability), 2 (Projections)");
        the.OneAgentOfEveryFamily();

        await the.TheLeaderEvaluates();

        the.AgentRunsOn(Projection, 2);
        the.AgentRunsOn(ExclusiveListener, 1);
        the.AgentRunsOn(RecurringSchedules, 1);
        the.AgentRunsOn(Durability, 1);
        the.NodeNeverLeads(2);
    }

    [BobcatSpec]
    public async Task with_no_node_running_projections_the_projections_run_nowhere_and_say_why()
    {
        the.Nodes("1 (Messaging), 2 (Messaging, Durability)");
        the.OneAgentOfEveryFamily();

        await the.TheLeaderEvaluates();

        the.AgentIsUnplaced(Projection, UnplacedReason.NoNodeForWorkload);
        the.AgentRunsOn(Durability, 2);
        the.TheLeaderWarns("need a node running the Projections workload");
    }

    [BobcatSpec]
    public async Task an_agent_on_a_node_outside_its_workload_is_moved()
    {
        the.Nodes("1 (Messaging, Projections), 2 (All)");
        the.OneAgentOfEveryFamily();
        the.AgentIsAlreadyRunning(Durability, 1);

        await the.TheLeaderEvaluates();

        the.TheLeaderMoves(Durability, 1, 2);
    }

    [BobcatSpec]
    public async Task a_pin_to_a_node_outside_the_workload_is_not_honored()
    {
        the.Nodes("1 (HTTP only), 2 (Messaging)");
        the.OneAgentOfEveryFamily();
        the.AnOperatorPinned(ExclusiveListener, 1);

        await the.TheLeaderEvaluates();

        the.AgentRunsOn(ExclusiveListener, 2);
        the.NodeRunsNothing(1);
    }

    [BobcatSpec]
    public async Task a_node_from_before_workloads_reads_as_all()
    {
        the.Nodes("1 (6.x), 2 (6.x), 3 (HTTP only)");
        the.OneAgentOfEveryFamily();

        await the.TheLeaderEvaluates();

        the.AgentRunsOnEither(Durability, 1, 2);
        the.AgentRunsOnEither(Projection, 1, 2);
        the.AgentRunsOnEither(ExclusiveListener, 1, 2);
        the.NodeRunsNothing(3);
    }

    [BobcatSpec]
    public async Task a_cluster_from_before_workloads_settles_without_churn()
    {
        the.Nodes("1 (6.x), 2 (6.x), 3 (All)");
        the.OneAgentOfEveryFamily();

        await the.TheLeaderEvaluates();
        await the.TheLeaderEvaluates();

        the.TheLeaderIssuesNoCommands();
    }

    [BobcatSpec]
    public async Task a_cluster_with_workloads_settles_without_churn()
    {
        the.Nodes("1 (HTTP only), 2 (Messaging, Projections), 3 (All)");
        the.OneAgentOfEveryFamily();

        await the.TheLeaderEvaluates();
        await the.TheLeaderEvaluates();

        the.TheLeaderIssuesNoCommands();
    }

    [BobcatSpec]
    public async Task the_durability_agents_wait_for_a_durability_node_when_the_only_one_leaves()
    {
        the.Nodes("1 (Messaging, Projections), 2 (All), 3 (HTTP only)");
        the.OneAgentOfEveryFamily();

        await the.TheLeaderEvaluates();
        the.AgentRunsOn(Durability, 2);

        the.NodeLeaves(2);
        await the.TheLeaderEvaluates();

        the.AgentIsUnplaced(Durability, UnplacedReason.NoNodeForWorkload);
        the.TheLeaderWarns("No node in this Wolverine cluster can run durability agents");
        the.TheLeaderWarns("need a node running the Durability workload");
        the.NodeRunsNothing(3);

        the.NodeJoins(4, "Messaging, Durability");
        await the.TheLeaderEvaluates();

        the.AgentRunsOn(Durability, 4);
    }

    [BobcatSpec]
    public async Task a_warmup_node_takes_only_the_new_projections_whatever_the_other_workloads()
    {
        the.Nodes("1 (Messaging, Durability), 2 (Messaging, Projections), 3 (warm-up)");
        the.NodeWarmsUpTheNextRelease(3, NextRelease);
        the.OneAgentOfEveryFamily();

        await the.TheLeaderEvaluates();

        the.AgentRunsOn(Projection, 2);
        the.AgentRunsOn(NextRelease, 3);
        the.AgentRunsOn(Durability, 1);
        the.AgentRunsOnEither(ExclusiveListener, 1, 2);
        the.NodeNeverLeads(3);
    }

    private const string NextRelease = "event-subscriptions://marten/main/main/trips/all/v2";

    [BobcatSpec]
    public async Task each_database_keeps_its_projections_together_on_the_projection_nodes()
    {
        the.Nodes("1 (Messaging, Durability), 2 (Messaging, Projections), 3 (Messaging, Projections)");
        the.SpreadOverDatabases("tenant1, tenant2");

        await the.TheLeaderEvaluates();

        the.EveryProjectionRunsOnEither(2, 3);
        the.TheProjectionsOfADatabaseRunTogether("tenant1");
        the.TheProjectionsOfADatabaseRunTogether("tenant2");

        // A durability agent follows its database's projections only to a node that can run it
        the.TheDurabilityAgentOfADatabaseRunsOn("tenant1", 1);
        the.TheDurabilityAgentOfADatabaseRunsOn("tenant2", 1);
    }

    [BobcatSpec]
    public async Task an_eligible_node_without_headroom_is_waiting_for_capacity_not_for_a_workload()
    {
        the.CapacityAwareAssignmentIsOn();
        the.Nodes("1 (HTTP only), 2 (Messaging, Durability)");
        the.NodeIsAtLoad(1, 10);
        the.NodeIsAtLoad(2, 95);
        the.OneAgentOfEveryFamily();

        await the.TheLeaderEvaluates();

        // Node 1 has the headroom, but does not run Messaging
        the.AgentIsUnplaced(ExclusiveListener, UnplacedReason.AwaitingCapacity);

        // No node runs Projections at all, so more headroom would not help
        the.AgentIsUnplaced(Projection, UnplacedReason.NoNodeForWorkload);
        the.NodeRunsNothing(1);
    }

    [BobcatSpec]
    public async Task the_only_durability_node_keeps_its_durability_agents_under_pressure()
    {
        the.CapacityAwareAssignmentIsOn();
        the.Nodes("1 (Messaging, Projections), 2 (All)");
        the.NodeIsAtLoad(2, 95);
        the.OneAgentOfEveryFamily();
        the.AgentIsAlreadyRunning(Durability, 2);

        await the.TheLeaderEvaluates();

        the.AgentRunsOn(Durability, 2);
        the.AgentRunsOn(ExclusiveListener, 1);
    }
}

/// <summary>
///     GH-5601, decision D4: a node that does not run Messaging never leads, the same as a warm-up node. The leader
///     runs the leader-pinned listeners, so it has to be a node that listens at all.
/// </summary>
[BobcatFeature("Only a node running Messaging leads")]
public class only_a_messaging_node_leads_5601
{
    private readonly WorkloadCluster the = new();

    [BobcatSpec]
    public void nodes_without_messaging_never_lead()
    {
        the.Nodes("1 (HTTP only), 2 (Messaging), 3 (Projections), 4 (All)");

        the.NodeNeverLeads(1);
        the.NodeMayLead(2);
        the.NodeNeverLeads(3);
        the.NodeMayLead(4);
    }

    [BobcatSpec]
    public void with_only_http_nodes_left_there_is_no_leader_and_every_node_says_so()
    {
        the.Nodes("1 (HTTP only), 2 (HTTP only)");

        the.NoNodeMayLead();

        the.AHeartbeatReadsTheNodeTable();
        the.ItWarns("No live node in this Wolverine cluster runs the Messaging workload");
    }

    [BobcatSpec]
    public void a_node_from_before_workloads_may_lead()
    {
        the.Nodes("1 (HTTP only), 2 (6.x)");

        the.NodeMayLead(2);
        the.AHeartbeatReadsTheNodeTable();
        the.ItDoesNotWarn("runs the Messaging workload");
    }
}
