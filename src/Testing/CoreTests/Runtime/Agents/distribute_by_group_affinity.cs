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
}
