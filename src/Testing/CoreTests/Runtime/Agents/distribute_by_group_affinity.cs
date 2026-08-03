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
        // No single node is capable of every member, so each member goes to its own least-loaded capable
        // node. A member NO node declares a capability for is NOT parked — for a sharded store an empty
        // candidate set is a stale-snapshot artifact, so it falls back to the least-loaded node overall
        // rather than silently stranding the shard (GH-3341).
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
        grid.AgentFor(t3).AssignedNode.ShouldNotBeNull(
            "an agent no node declares is still assigned to a surviving node, never silently stranded (GH-3341)");
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
    public void a_shard_orphaned_by_a_departed_node_is_reassigned_not_stranded()
    {
        // GH-3341: on scale-down, a shard database whose agents ran on a now-departed node — and whose
        // URIs are absent from every SURVIVING node's capability snapshot (each node captures its
        // event-subscription capabilities once at startup, so a database provisioned after the survivors
        // started is missing from their snapshots even though every node can run it) — was left silently
        // unassigned. The shard stopped projecting with no running agent, no error log, and no self-heal
        // until a rolling restart. It must instead be reassigned to a surviving node.
        var db1 = new[] { Agent("db1", "t1"), Agent("db1", "t2") };
        var orphaned = new[] { Agent("db2", "t1"), Agent("db2", "t2") };

        var grid = new AssignmentGrid();
        // Two survivors with DIVERGENT snapshots (node 2 started later and also knows db3), so
        // AllNodesHaveSameCapabilities is false and the capability-matching branch runs. Neither lists db2.
        var node1 = grid.WithNode(1, Guid.NewGuid()).HasCapabilities(db1);
        node1.Running(db1);
        grid.WithNode(2, Guid.NewGuid()).HasCapabilities(db1.Append(Agent("db3", "t1")));

        // db2's agents were running on a 3rd node that just departed; the leader re-enumerates them via
        // AllKnownAgentsAsync and adds them unassigned. No surviving node declares them as capabilities.
        grid.WithAgents(orphaned);

        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        foreach (var uri in orphaned)
        {
            grid.AgentFor(uri).AssignedNode.ShouldNotBeNull(
                "an orphaned shard must be reassigned to a surviving node, not silently stranded (GH-3341)");
        }

        // And db2 stays whole on one node — the connection-pool affinity this method exists to provide.
        orphaned.Select(u => grid.AgentFor(u).AssignedNode).Distinct().Count()
            .ShouldBe(1, "the orphaned shard's agents must be co-located on a single node");
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

    // event-subscriptions://{type}/{name}/{databaseId}/{projection}/{shardKey}/v{version}/{tenant}
    // — the real EventSubscriptionAgentFamily.UriFor grammar, so the version sits in its own segment.
    private static Uri VersionedAgent(string db, uint version, string tenant) =>
        new($"event-subscriptions://marten/main/{db}/Proj/All/v{version}/{tenant}");

    [Fact]
    public void a_version_bump_splits_a_group_between_the_old_and_new_version_nodes()
    {
        // A projection version bump on a sharded store: one shard database's group spans the previous
        // version's agent — declared by, and RUNNING on, the blue node — and the new version's agent,
        // declared only by the green node. No node is capable of the whole group, so the members must
        // fall back individually: the old version stays on blue, the new version goes to green.
        //
        // This is the intersection of the two cases above, and it is what a blue/green deployment of a
        // sharded store looks like at every evaluation for the whole rollout, not just transiently.
        var previous = VersionedAgent("db1", 22, "t1");
        var bumped = VersionedAgent("db1", 23, "t1");

        var grid = new AssignmentGrid();
        var blue = grid.WithNode(1, Guid.NewGuid()).HasCapabilities(new[] { previous });
        blue.Running(previous);
        var green = grid.WithNode(2, Guid.NewGuid()).HasCapabilities(new[] { bumped });

        grid.WithAgents(previous, bumped);

        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        grid.AgentFor(previous).AssignedNode.ShouldBe(blue,
            "the previous version keeps running where it is");
        grid.AgentFor(bumped).AssignedNode.ShouldBe(green,
            "the new version's agent may only run on the node that declares it — the blue node cannot build it");
    }

    [Fact]
    public void a_settled_blue_green_split_does_not_churn_on_the_next_evaluation()
    {
        // GH-3785 counted ~45,000 ReassignAgent decisions in six minutes during a rollout ramp. Whatever
        // else contributes to that, the placement itself must be a fixed point: re-evaluating the settled
        // split state — same fleet, same capabilities, everything running exactly where the previous
        // evaluation put it — must move nothing. The partition incumbent rule (members[0].AssignedNode,
        // all members on one node) is subtle enough to regress silently, so pin it.
        var databases = new[] { "db1", "db2", "db3", "db4" };
        var tenants = new[] { "t1", "t2" };

        Uri Unchanged(string db, string tenant) =>
            new($"event-subscriptions://marten/main/{db}/Other/All/v7/{tenant}");

        Uri[] Across(Func<string, string, Uri> agent) =>
            databases.SelectMany(db => tenants.Select(t => agent(db, t))).ToArray();

        var previous = Across((db, t) => VersionedAgent(db, 22, t));
        var bumped = Across((db, t) => VersionedAgent(db, 23, t));
        var unchanged = Across(Unchanged);

        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();

        AssignmentGrid BuildGrid(out Dictionary<Guid, AssignmentGrid.Node> byId)
        {
            var grid = new AssignmentGrid();
            var blues = new[] { grid.WithNode(1, ids[0]), grid.WithNode(2, ids[1]) };
            var greens = new[] { grid.WithNode(3, ids[2]), grid.WithNode(4, ids[3]) };
            foreach (var blue in blues) blue.HasCapabilities(previous.Concat(unchanged));
            foreach (var green in greens) green.HasCapabilities(bumped.Concat(unchanged));
            byId = blues.Concat(greens).ToDictionary(n => n.NodeId);
            return grid;
        }

        var first = BuildGrid(out _);
        first.WithAgents(previous.Concat(bumped).Concat(unchanged).ToArray());
        first.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        var placement = first.AllAgents.ToDictionary(a => a.Uri, a => a.AssignedNode!.NodeId);

        // Second evaluation: the first one's outcome is now reality — every agent RUNNING where it landed.
        var second = BuildGrid(out var nodesById);
        foreach (var byNode in placement.GroupBy(kv => kv.Value))
        {
            nodesById[byNode.Key].Running(byNode.Select(kv => kv.Key).ToArray());
        }

        second.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        var moved = second.AllAgents
            .Where(a => a.AssignedNode!.NodeId != placement[a.Uri])
            .Select(a => a.Uri.ToString())
            .ToList();

        moved.ShouldBeEmpty("a settled split state must be a fixed point of the placement, not churn");
    }

    [Fact]
    public void a_rolling_restart_keeps_every_database_on_at_most_two_hosts()
    {
        // The partition key is the EXACT set of declaring node ids — stricter than "a common capable node
        // exists", and flagged in the PR as a deliberate judgement call. This pins the invariant that makes
        // the strictness safe to keep (or later relax): in a rolling restart, where capability sets overlap
        // but are NOT equal — node 1 still on the old build and running everything, nodes 2 and 3 already
        // restarted onto the new one — a database still lands on at most two hosts, so the strict key costs
        // no extra connection pools. Anyone coarsening the key can refactor against this safely.
        var databases = new[] { "db1", "db2", "db3" };
        var tenants = new[] { "t1", "t2" };

        Uri Unchanged(string db, string tenant) =>
            new($"event-subscriptions://marten/main/{db}/Other/All/v7/{tenant}");

        Uri[] Across(Func<string, string, Uri> agent) =>
            databases.SelectMany(db => tenants.Select(t => agent(db, t))).ToArray();

        var previous = Across((db, t) => VersionedAgent(db, 22, t));
        var bumped = Across((db, t) => VersionedAgent(db, 23, t));
        var unchanged = Across(Unchanged);

        var grid = new AssignmentGrid();
        var old = grid.WithNode(1, Guid.NewGuid()).HasCapabilities(previous.Concat(unchanged));
        old.Running(previous.Concat(unchanged).ToArray()); // was the whole cluster before the roll began
        grid.WithNode(2, Guid.NewGuid()).HasCapabilities(bumped.Concat(unchanged));
        grid.WithNode(3, Guid.NewGuid()).HasCapabilities(bumped.Concat(unchanged));

        grid.WithAgents(previous.Concat(bumped).Concat(unchanged).ToArray());

        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        grid.AllAgents.ShouldAllBe(a => a.AssignedNode != null);

        foreach (var db in databases)
        {
            var hosts = tenants
                .SelectMany(t => new[] { VersionedAgent(db, 22, t), VersionedAgent(db, 23, t), Unchanged(db, t) })
                .Select(uri => grid.AgentFor(uri).AssignedNode)
                .Distinct()
                .ToList();

            hosts.Count.ShouldBeLessThanOrEqualTo(2,
                $"{db} landed on {hosts.Count} hosts — a rolling restart must not cost a third pool set per database");
        }
    }

    [Fact]
    public void asymmetric_fleets_strand_nothing_and_load_every_capable_node()
    {
        // A brand-new green fleet that declares ONLY the bumped agents (it has not been anything else yet)
        // joins a blue fleet running everything. The per-node ceiling is computed over ALL nodes — including
        // the ones incapable of most of the work — so this shape is where ceiling arithmetic would go wrong
        // first: nothing may be left unassigned, every bumped agent must land green, and neither green node
        // may sit idle while its twin takes the whole new version.
        var databases = Enumerable.Range(1, 8).Select(i => $"db{i}").ToArray();
        var tenants = new[] { "t1", "t2", "t3" };

        Uri[] Across(uint version) =>
            databases.SelectMany(db => tenants.Select(t => VersionedAgent(db, version, t))).ToArray();

        var previous = Across(22);
        var bumped = Across(23);

        var grid = new AssignmentGrid();
        var blues = new[] { grid.WithNode(1, Guid.NewGuid()), grid.WithNode(2, Guid.NewGuid()) };
        var greens = new[] { grid.WithNode(3, Guid.NewGuid()), grid.WithNode(4, Guid.NewGuid()) };
        foreach (var blue in blues) blue.HasCapabilities(previous);
        blues[0].Running(previous.Take(previous.Length / 2).ToArray());
        blues[1].Running(previous.Skip(previous.Length / 2).ToArray());
        foreach (var green in greens) green.HasCapabilities(bumped);

        grid.WithAgents(previous.Concat(bumped).ToArray());

        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        grid.AllAgents.ShouldAllBe(a => a.AssignedNode != null);

        foreach (var uri in bumped)
        {
            greens.ShouldContain(grid.AgentFor(uri).AssignedNode!,
                $"{uri} may only run on the fleet that declares it");
        }

        greens.Select(g => g.ForScheme("event-subscriptions").Count())
            .ShouldAllBe(count => count > 0, "no green node may sit idle while its twin hosts the whole new version");
    }

    [Fact]
    public void a_split_group_costs_only_as_many_nodes_as_the_capability_split_forces()
    {
        // The same version bump, now with the projections whose version did NOT change also in the group.
        // Every node declares those, so nothing stops them landing on a third node — and what a shard
        // database costs in connection pools is the number of DISTINCT nodes holding any of its agents, so
        // a third host is a third pool set on that database. A split group must still occupy only as many
        // nodes as the capability split forces: one per version.
        var databases = new[] { "db1", "db2", "db3", "db4" };
        var tenants = new[] { "t1", "t2" };

        Uri Unchanged(string db, string tenant) =>
            new($"event-subscriptions://marten/main/{db}/Other/All/v7/{tenant}");

        Uri[] Across(Func<string, string, Uri> agent) =>
            databases.SelectMany(db => tenants.Select(t => agent(db, t))).ToArray();

        var previous = Across((db, t) => VersionedAgent(db, 22, t));
        var bumped = Across((db, t) => VersionedAgent(db, 23, t));
        var unchanged = Across(Unchanged);

        var grid = new AssignmentGrid();
        var blues = new[] { grid.WithNode(1, Guid.NewGuid()), grid.WithNode(2, Guid.NewGuid()) };
        var greens = new[] { grid.WithNode(3, Guid.NewGuid()), grid.WithNode(4, Guid.NewGuid()) };

        foreach (var blue in blues) blue.HasCapabilities(previous.Concat(unchanged));
        foreach (var green in greens) green.HasCapabilities(bumped.Concat(unchanged));

        grid.WithAgents(previous.Concat(bumped).Concat(unchanged).ToArray());

        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        foreach (var db in databases)
        {
            var hosts = tenants
                .SelectMany(t => new[] { VersionedAgent(db, 22, t), VersionedAgent(db, 23, t), Unchanged(db, t) })
                .Select(uri => grid.AgentFor(uri).AssignedNode)
                .Distinct()
                .ToList();

            hosts.Count.ShouldBe(2,
                $"{db} must sit on exactly two nodes — one per version — so it attracts two pool sets, not three");
            hosts.ShouldContain(host => blues.Contains(host!), $"{db}'s previous version must be on a blue node");
            hosts.ShouldContain(host => greens.Contains(host!), $"{db}'s new version must be on a green node");
        }
    }
}

