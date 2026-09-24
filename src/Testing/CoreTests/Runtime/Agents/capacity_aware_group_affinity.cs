using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-4592. Capacity awareness reaching the distribution path that the GH-3959 incident shape actually
/// runs on: a multi-database event store, whose shard databases are placed as indivisible groups by
/// DistributeByGroupAffinity.
///
/// <para>The governing rule, and the difference from DistributeEvenly: here capacity is a PREFERENCE and
/// never a filter. Every node is a candidate in the even path, so refusing the overloaded ones can only
/// delay an agent. Here the candidate set is already narrowed by declared capabilities, by the GH-3341
/// rescue and by the GH-4562 grandfathering — emptying it means a shard database with no running agent,
/// no log and no self-heal until a restart.</para>
/// </summary>
public class capacity_aware_group_affinity
{
    private static string DatabaseKey(Uri uri) => uri.Segments[2].Trim('/');

    private static Uri Agent(string db, string tenant) =>
        new($"event-subscriptions://marten/main/{db}/Proj:All:{tenant}");

    private static Uri[] Database(string db, params string[] tenants) =>
        tenants.Select(t => Agent(db, t)).ToArray();

    private static AssignmentGrid.Node? HostOf(AssignmentGrid grid, string db, params string[] tenants)
    {
        var nodes = tenants.Select(t => grid.AgentFor(Agent(db, t)).AssignedNode).Distinct().ToList();
        nodes.Count.ShouldBe(1, $"all agents of {db} must share one node");
        return nodes[0];
    }

    [Fact]
    public void a_new_database_prefers_the_node_with_headroom()
    {
        var grid = new AssignmentGrid();
        var busy = grid.WithNode(1, Guid.NewGuid());
        var idle = grid.WithNode(2, Guid.NewGuid());

        busy.LoadFactor = 95;
        busy.IsOverloaded = true;
        busy.IsAcceptingAgents = false;
        idle.LoadFactor = 10;

        grid.WithAgents(Database("db1", "t1", "t2"));
        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        HostOf(grid, "db1", "t1", "t2").ShouldBe(idle);
    }

    /// <summary>
    /// The rule that separates this path from the even one. An overloaded node is still better than no
    /// node: parking a shard database stops it projecting, and nothing restarts it until pressure clears.
    /// </summary>
    [Fact]
    public void a_database_is_still_placed_when_no_node_has_headroom()
    {
        var grid = new AssignmentGrid();

        foreach (var i in new[] { 1, 2 })
        {
            var node = grid.WithNode(i, Guid.NewGuid());
            node.LoadFactor = 99;
            node.IsOverloaded = true;
            node.IsAcceptingAgents = false;
        }

        grid.WithAgents(Database("db1", "t1", "t2"));
        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        HostOf(grid, "db1", "t1", "t2").ShouldNotBeNull();
    }

    [Fact]
    public void an_overloaded_incumbent_hands_a_database_to_a_node_with_headroom()
    {
        var grid = new AssignmentGrid();

        var hot = grid.WithNode(1, Guid.NewGuid()).Running(Database("db1", "t1", "t2"));
        var cool = grid.WithNode(2, Guid.NewGuid()).Running(Database("db2", "t1", "t2"));

        hot.IsOverloaded = true;
        hot.IsAcceptingAgents = false;
        hot.LoadFactor = 95;
        cool.LoadFactor = 20;

        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        HostOf(grid, "db1", "t1", "t2").ShouldBe(cool);
    }

    /// <summary>
    /// GH-4590's lesson applied to an indivisible unit: shedding is only ever a move. With nowhere for
    /// the database to go, taking it off the overloaded node would simply stop it.
    /// </summary>
    [Fact]
    public void an_overloaded_incumbent_keeps_a_database_when_nothing_can_take_it()
    {
        var grid = new AssignmentGrid();

        var hot = grid.WithNode(1, Guid.NewGuid()).Running(Database("db1", "t1", "t2"));
        var alsoHot = grid.WithNode(2, Guid.NewGuid()).Running(Database("db2", "t1", "t2"));

        foreach (var node in grid.Nodes)
        {
            node.IsOverloaded = true;
            node.IsAcceptingAgents = false;
            node.LoadFactor = 96;
        }

        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        HostOf(grid, "db1", "t1", "t2").ShouldBe(hot);
        HostOf(grid, "db2", "t1", "t2").ShouldBe(alsoHot);
    }

    /// <summary>
    /// A node hosting many databases must not shed all of them at once: that is a connection-pool
    /// stampede plus a round of catch-up per database, to relieve pressure that is re-sampled every
    /// heartbeat anyway.
    /// </summary>
    [Fact]
    public void shedding_is_bounded_per_evaluation()
    {
        var grid = new AssignmentGrid();

        // Both nodes hold four databases, so the per-node ceiling is four and NOTHING is forced to move
        // by the balance rules. Every move here is the capacity shed, which makes the budget the only
        // thing deciding how many happen.
        var hot = grid.WithNode(1, Guid.NewGuid())
            .Running(Database("db1", "t1"))
            .Running(Database("db2", "t1"))
            .Running(Database("db3", "t1"))
            .Running(Database("db4", "t1"));
        var cool = grid.WithNode(2, Guid.NewGuid())
            .Running(Database("db5", "t1"))
            .Running(Database("db6", "t1"))
            .Running(Database("db7", "t1"))
            .Running(Database("db8", "t1"));

        hot.IsOverloaded = true;
        hot.IsAcceptingAgents = false;
        hot.LoadFactor = 95;
        cool.LoadFactor = 5;

        grid.OverloadShedBatchSize = 1;
        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        var moved = new[] { "db1", "db2", "db3", "db4" }
            .Count(db => HostOf(grid, db, "t1") == cool);

        moved.ShouldBe(1);
    }

    [Fact]
    public void the_shed_budget_is_configurable()
    {
        var grid = new AssignmentGrid();

        var hot = grid.WithNode(1, Guid.NewGuid())
            .Running(Database("db1", "t1"))
            .Running(Database("db2", "t1"))
            .Running(Database("db3", "t1"))
            .Running(Database("db4", "t1"));
        var cool = grid.WithNode(2, Guid.NewGuid())
            .Running(Database("db5", "t1"))
            .Running(Database("db6", "t1"))
            .Running(Database("db7", "t1"))
            .Running(Database("db8", "t1"));

        hot.IsOverloaded = true;
        hot.IsAcceptingAgents = false;
        hot.LoadFactor = 95;
        cool.LoadFactor = 5;

        grid.OverloadShedBatchSize = 3;
        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        new[] { "db1", "db2", "db3", "db4" }
            .Count(db => HostOf(grid, db, "t1") == cool)
            .ShouldBe(3);
    }

    /// <summary>
    /// Pressure never justifies splitting a database — that is the whole reason this method exists.
    /// </summary>
    [Fact]
    public void a_database_is_never_split_to_relieve_pressure()
    {
        var grid = new AssignmentGrid();

        var hot = grid.WithNode(1, Guid.NewGuid()).Running(Database("db1", "t1", "t2", "t3", "t4"));
        grid.WithNode(2, Guid.NewGuid());

        hot.IsOverloaded = true;
        hot.IsAcceptingAgents = false;
        hot.LoadFactor = 99;

        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        HostOf(grid, "db1", "t1", "t2", "t3", "t4").ShouldNotBeNull();
    }

    /// <summary>
    /// GH-3341's rescue must survive capacity: a partition no node DECLARES is assignable to any node, and
    /// overload must not turn "any node" back into none.
    /// </summary>
    [Fact]
    public void the_undeclared_partition_rescue_still_places_the_group_under_pressure()
    {
        var grid = new AssignmentGrid();

        // Heterogeneous capabilities so the capability-matched path is taken, and db2 is declared by
        // nobody -- the stale-snapshot shape GH-3341 describes.
        var node1 = grid.WithNode(1, Guid.NewGuid()).HasCapabilities(Database("db1", "t1", "t2"));
        var node2 = grid.WithNode(2, Guid.NewGuid()).HasCapabilities(Database("db1", "t1"));

        foreach (var node in grid.Nodes)
        {
            node.IsOverloaded = true;
            node.IsAcceptingAgents = false;
            node.LoadFactor = 97;
        }

        grid.WithAgents(Database("db1", "t1", "t2"));
        grid.WithAgents(Database("db2", "t1", "t2"));

        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        HostOf(grid, "db2", "t1", "t2").ShouldNotBeNull();
    }

    /// <summary>
    /// The off switch. With capacity-aware assignment disabled nothing sets these flags, so the ordering
    /// has to collapse to exactly the pre-GH-4592 load/IsLeader/AssignedId behavior.
    /// </summary>
    [Fact]
    public void placement_is_unchanged_when_no_node_advertises_anything()
    {
        var grid = new AssignmentGrid();
        var node1 = grid.WithNode(1, Guid.NewGuid());
        var node2 = grid.WithNode(2, Guid.NewGuid());

        grid.Nodes.ShouldAllBe(x => x.IsAcceptingAgents && x.LoadFactor == null);

        grid.WithAgents(Database("db1", "t1", "t2"));
        grid.WithAgents(Database("db2", "t1", "t2"));

        grid.DistributeByGroupAffinity("event-subscriptions", DatabaseKey);

        // Largest-first onto the least loaded, deterministic tie-break on node id: one database each.
        HostOf(grid, "db1", "t1", "t2").ShouldBe(node1);
        HostOf(grid, "db2", "t1", "t2").ShouldBe(node2);
    }
}
