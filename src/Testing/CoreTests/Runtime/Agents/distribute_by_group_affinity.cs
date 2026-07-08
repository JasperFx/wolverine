using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

// JasperFx/marten#4806: AssignmentGrid.DistributeByGroupAffinity keeps every agent of a group (e.g. one
// shard database) on the same node, and spreads whole groups across nodes — so a node opens connection
// pools only to the databases it owns, instead of every node touching every shard database.
public class distribute_by_group_affinity
{
    // Group key = the database segment of an event-subscriptions agent URI:
    // event-subscriptions://{type}/{name}/{databaseId}/{shard...}  ->  Segments[2].
    private static string DatabaseKey(Uri uri) => uri.Segments[2].Trim('/');

    private static Uri Agent(string db, string tenant) =>
        new($"event-subscriptions://marten/main/{db}/Proj:All:{tenant}");

    [Fact]
    public void keeps_a_databases_agents_together_and_spreads_databases_across_nodes()
    {
        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid());
        grid.WithNode(2, Guid.NewGuid());

        // 4 shard databases, 3 tenants each = 12 per-tenant agents.
        var agents = new List<Uri>();
        foreach (var db in new[] { "db1", "db2", "db3", "db4" })
        foreach (var tenant in new[] { "t1", "t2", "t3" })
            agents.Add(Agent(db, tenant));

        grid.WithAgents(agents.ToArray());

        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        // Every tenant agent of a database lands on exactly one node (the database is never split).
        foreach (var db in new[] { "db1", "db2", "db3", "db4" })
        {
            var nodes = new[] { "t1", "t2", "t3" }
                .Select(t => grid.AgentFor(Agent(db, t)).AssignedNode)
                .Distinct()
                .ToList();

            nodes.Count.ShouldBe(1, $"all agents of {db} must be on a single node");
            nodes[0].ShouldNotBeNull();
        }

        // All agents are assigned, and the 4 databases are spread across both nodes (2 each).
        grid.AllAgents.ShouldAllBe(a => a.AssignedNode != null);
        var perNode = grid.Nodes.Select(n => n.ForScheme("event-subscriptions").Count()).OrderBy(x => x).ToList();
        perNode.ShouldBe(new[] { 6, 6 });
    }

    [Fact]
    public void single_node_takes_every_agent()
    {
        var grid = new AssignmentGrid();
        var node = grid.WithNode(1, Guid.NewGuid());

        grid.WithAgents(Agent("db1", "t1"), Agent("db1", "t2"), Agent("db2", "t1"));

        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        node.ForScheme("event-subscriptions").Count().ShouldBe(3);
    }

    [Fact]
    public void a_heavy_database_stays_on_one_node_and_light_groups_balance_around_it()
    {
        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid());
        grid.WithNode(2, Guid.NewGuid());

        // One heavy database (4 tenants) + two light ones (1 tenant each). Largest-first placement puts the
        // heavy group on one node and both light groups on the other — totals stay as balanced as whole
        // groups allow, and a database is never split across nodes.
        var agents = new List<Uri>();
        foreach (var tenant in new[] { "t1", "t2", "t3", "t4" })
            agents.Add(Agent("dbHeavy", tenant));
        agents.Add(Agent("dbLight1", "t1"));
        agents.Add(Agent("dbLight2", "t1"));

        grid.WithAgents(agents.ToArray());

        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        grid.AllAgents.ShouldAllBe(a => a.AssignedNode != null);

        var heavyNode = grid.AgentFor(Agent("dbHeavy", "t1")).AssignedNode;
        new[] { "t2", "t3", "t4" }
            .Select(t => grid.AgentFor(Agent("dbHeavy", t)).AssignedNode)
            .ShouldAllBe(n => n == heavyNode);

        grid.AgentFor(Agent("dbLight1", "t1")).AssignedNode.ShouldNotBe(heavyNode);
        grid.AgentFor(Agent("dbLight2", "t1")).AssignedNode.ShouldNotBe(heavyNode);
    }

    [Fact]
    public void a_group_lands_only_on_a_node_capable_of_running_it()
    {
        // Blue/green: node capabilities differ, so placement must respect them exactly like
        // DistributeEvenlyWithBlueGreenSemantics — the db1 group may only land on the capable node 2,
        // even though the load-based choice alone would prefer the emptier node 1.
        var db1Agents = new[] { Agent("db1", "t1"), Agent("db1", "t2") };

        var grid = new AssignmentGrid();
        grid.WithNode(1, Guid.NewGuid());
        var node2 = grid.WithNode(2, Guid.NewGuid()).HasCapabilities(db1Agents);

        grid.WithAgents(db1Agents);

        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        foreach (var uri in db1Agents)
        {
            grid.AgentFor(uri).AssignedNode.ShouldBe(node2,
                "the group must land on the only node that declares its agents as capabilities");
        }
    }

    [Fact]
    public void when_no_node_can_host_the_whole_group_members_fall_back_individually()
    {
        // Mirrors the blue/green even path: no single node is capable of every member, so each member goes
        // to its own least-loaded capable node, and a member NO node declares stays unassigned (parked).
        var t1 = Agent("db1", "t1");
        var t2 = Agent("db1", "t2");
        var t3 = Agent("db1", "t3");

        var grid = new AssignmentGrid();
        var node1 = grid.WithNode(1, Guid.NewGuid()).HasCapabilities(new[] { t1 });
        var node2 = grid.WithNode(2, Guid.NewGuid()).HasCapabilities(new[] { t2 });

        grid.WithAgents(t1, t2, t3);

        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        grid.AgentFor(t1).AssignedNode.ShouldBe(node1);
        grid.AgentFor(t2).AssignedNode.ShouldBe(node2);
        grid.AgentFor(t3).AssignedNode.ShouldBeNull("an agent no node declares a capability for is parked, exactly like the even path");
    }

    [Fact]
    public void a_node_already_running_a_groups_agents_stays_a_candidate_despite_a_stale_capability_snapshot()
    {
        // Capability snapshots are persisted once at node startup, so a node that started before the
        // tenant databases were provisioned declares NO event-subscription capabilities even though it is
        // running all the agents (MartenTests' MultiTenantContext starts exactly this way). The even paths
        // tolerate that by leaving running agents in place; group placement must grandfather such a node
        // as a candidate too, or it is starved and another node ends up with several whole databases.
        var databases = new[] { "db1", "db2", "db3" };
        var tenants = new[] { "t1", "t2", "t3" };
        var all = databases.SelectMany(db => tenants.Select(t => Agent(db, t))).ToArray();

        var grid = new AssignmentGrid();
        var node1 = grid.WithNode(1, Guid.NewGuid());
        node1.Running(all); // was the only node; no capabilities declared
        grid.WithNode(2, Guid.NewGuid()).HasCapabilities(all);
        grid.WithNode(3, Guid.NewGuid()).HasCapabilities(all);

        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        // Every database whole on one node, and the three databases spread across all three nodes --
        // including the stale-capability node.
        var hosts = databases.Select(db =>
        {
            var nodes = tenants.Select(t => grid.AgentFor(Agent(db, t)).AssignedNode).Distinct().ToList();
            nodes.Count.ShouldBe(1, $"all agents of {db} must be on a single node");
            return nodes[0]!;
        }).ToList();

        hosts.Distinct().Count().ShouldBe(3, "three equal databases across three nodes must land one per node");
    }

    [Fact]
    public void an_incumbent_node_keeps_its_group_up_to_the_ceiling()
    {
        // Mid-convergence snapshot of the MultiTenantContext scenario: node 1 (stale capability snapshot,
        // declares nothing) still runs db1's agent, node 2 runs db2's and db3's, node 3 just joined. The
        // even path resolves this to 1/1/1 by moving only the over-ceiling extra; group placement must do
        // the same — keep incumbents up to the ceiling, move only db3 — instead of reshuffling from
        // scratch, which starves the stale-capability node forever.
        var g1 = Agent("db1", "only");
        var g2 = Agent("db2", "only");
        var g3 = Agent("db3", "only");

        var grid = new AssignmentGrid();
        var node1 = grid.WithNode(1, Guid.NewGuid());
        node1.Running(g1); // no declared capabilities
        var node2 = grid.WithNode(2, Guid.NewGuid()).HasCapabilities(new[] { g1, g2, g3 });
        node2.Running(g2, g3);
        var node3 = grid.WithNode(3, Guid.NewGuid()).HasCapabilities(new[] { g1, g2, g3 });

        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        grid.AgentFor(g1).AssignedNode.ShouldBe(node1, "the incumbent keeps its group despite the stale capability snapshot");
        grid.AgentFor(g2).AssignedNode.ShouldBe(node2, "an under-ceiling incumbent keeps its group");
        grid.AgentFor(g3).AssignedNode.ShouldBe(node3, "only the over-ceiling group moves, to the empty node");
    }
}

