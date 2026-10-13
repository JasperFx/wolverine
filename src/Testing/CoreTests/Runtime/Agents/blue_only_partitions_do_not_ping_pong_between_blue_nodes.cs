using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-4999. A blue/green warm-up of a sharded store in which the previous version's agents can only run on the blue
/// nodes. Blue holds more than ceil(all agents / all nodes) once the shared agents are counted too -- more so
/// while an overloaded green node takes nothing -- so the per-node ceiling computed over the whole fleet can
/// never be met there. The blue-only partitions evaluated last failed the incumbent check every time and went to
/// whichever blue node sat in the lowest load band that heartbeat. In the field they moved between the blue nodes
/// on every evaluation while the shared ones moved once.
/// </summary>
public class blue_only_partitions_do_not_ping_pong_between_blue_nodes
{
    private const string Scheme = "event-subscriptions";

    private static readonly (string Name, uint Version)[] BlueOnly = [("provided-cares", 33), ("pending-claims", 25)];
    private static readonly (string Name, uint Version)[] GreenOnly = [("provided-cares", 34), ("pending-claims", 26)];
    private static readonly (string Name, uint Version)[] Shared =
        [("invoices", 7), ("claim-lines", 12), ("returns", 4), ("timeline", 9)];

    // Uneven on purpose: shard databases carry very different numbers of tenants.
    private static readonly (string Db, int Tenants)[] Databases = Enumerable.Range(1, 24)
        .Select(i => ($"shard_{i:D3}", 1 + i * 7 % 11))
        .ToArray();

    private static Uri[] agents(params (string Name, uint Version)[] projections) => Databases
        .SelectMany(d => Enumerable.Range(1, d.Tenants).SelectMany(t => projections.Select(p =>
            new Uri($"{Scheme}://marten/main/{d.Db}/{p.Name}/All/v{p.Version}/{d.Db}_t{t}"))))
        .ToArray();

    private static readonly Uri[] Previous = agents(BlueOnly);
    private static readonly Uri[] Bumped = agents(GreenOnly);
    private static readonly Uri[] Unchanged = agents(Shared);

    private static readonly Guid[] BlueIds = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
    private static readonly Guid[] GreenIds = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();

    private static AssignmentGrid evaluate(Dictionary<Uri, Guid> running, bool withGreen, int heartbeat = 0)
    {
        var grid = new AssignmentGrid();
        var nodes = new Dictionary<Guid, AssignmentGrid.Node>();

        // Readings move between heartbeats; the blue nodes trade load bands from one evaluation to the next.
        double[] blueLoads = [44, 52, 48, 58];
        for (var i = 0; i < BlueIds.Length; i++)
        {
            var blue = grid.WithNode(i + 1, BlueIds[i]).HasCapabilities(Previous.Concat(Unchanged));
            blue.LoadFactor = blueLoads[(i + heartbeat) % blueLoads.Length];
            blue.IsLeader = i == 0;
            nodes[BlueIds[i]] = blue;
        }

        if (withGreen)
        {
            double[] greenLoads = [31, 36, 77];
            for (var i = 0; i < GreenIds.Length; i++)
            {
                var green = grid.WithNode(i + 5, GreenIds[i]).HasCapabilities(Bumped.Concat(Unchanged));
                green.LoadFactor = greenLoads[i];
                nodes[GreenIds[i]] = green;
            }

            var overloaded = nodes[GreenIds[2]];
            overloaded.IsOverloaded = true;
            overloaded.IsAcceptingAgents = false;
        }

        foreach (var byNode in running.Where(x => nodes.ContainsKey(x.Value)).GroupBy(x => x.Value))
        {
            nodes[byNode.Key].Running(byNode.Select(x => x.Key).ToArray());
        }

        grid.WithAgents(withGreen ? Previous.Concat(Bumped).Concat(Unchanged).ToArray() : Previous.Concat(Unchanged).ToArray());
        grid.DistributeByGroupAffinity(Scheme, EventSubscriptionAgentFamily.DatabaseKeyOf);

        return grid;
    }

    private static Dictionary<Uri, Guid> placementOf(AssignmentGrid grid) =>
        grid.AllAgents.Where(a => a.AssignedNode != null).ToDictionary(a => a.Uri, a => a.AssignedNode!.NodeId);

    [Fact]
    public void blue_only_agents_settle_while_green_warms_up_next_to_an_overloaded_green_node()
    {
        // Before the rollout: the blue fleet runs the previous version and the shared projections.
        var placement = placementOf(evaluate(new Dictionary<Uri, Guid>(), withGreen: false));

        var blueOnlyMoves = new List<int>();
        for (var evaluation = 0; evaluation < 12; evaluation++)
        {
            var next = placementOf(evaluate(placement, withGreen: true, heartbeat: evaluation));

            next.Keys.ShouldBe(Previous.Concat(Bumped).Concat(Unchanged), ignoreOrder: true,
                "every agent has a home on every evaluation");
            Previous.ShouldAllBe(uri => BlueIds.Contains(next[uri]));

            blueOnlyMoves.Add(Previous.Count(uri => next[uri] != placement[uri]));
            placement = next;
        }

        // The evaluation green joins in may rebalance the previous version once. After that it must be a fixed point,
        // however long the overloaded green node keeps shedding its shared partitions.
        blueOnlyMoves.Skip(1).ShouldAllBe(moves => moves == 0,
            $"blue-only agents moved per evaluation: {string.Join(", ", blueOnlyMoves)}");
    }

    private static Uri[] database(string db, int tenants) => Enumerable.Range(1, tenants)
        .Select(t => new Uri($"{Scheme}://marten/main/{db}/invoices/All/v1/{db}_t{t}"))
        .ToArray();

    /// <summary>
    /// When a partition that fits nowhere does move, it goes to the accepting node carrying the FEWEST agents,
    /// not to the lighter node in the lowest load band. Band order sent it to a node that was lighter than the
    /// incumbent but still heavier than another one, so the next evaluation moved it a second time.
    /// </summary>
    [Fact]
    public void an_overflow_partition_moves_once_straight_to_the_lightest_accepting_node()
    {
        // 27 agents over 3 nodes puts the ceiling at 9. db1, db2 and db3 each keep their node; "overflow" then
        // fits under the ceiling nowhere and its incumbent is the heaviest node.
        var db1 = database("db1", 9);
        var db2 = database("db2", 7);
        var db3 = database("db3", 6);
        var overflow = database("overflow", 5);

        var placement = new Dictionary<Uri, int>();
        foreach (var uri in db1.Concat(overflow)) placement[uri] = 1;
        foreach (var uri in db2) placement[uri] = 2;
        foreach (var uri in db3) placement[uri] = 3;

        var hosts = new List<int>();
        for (var evaluation = 0; evaluation < 3; evaluation++)
        {
            var grid = new AssignmentGrid();

            // Node 2 sits in the lowest load band but carries more agents than node 3.
            double[] loads = [40, 20, 60];
            foreach (var id in new[] { 1, 2, 3 })
            {
                var node = grid.WithNode(id, Guid.NewGuid());
                node.LoadFactor = loads[id - 1];
                node.Running(placement.Where(x => x.Value == id).Select(x => x.Key).ToArray());
            }

            grid.DistributeByGroupAffinity(Scheme, EventSubscriptionAgentFamily.DatabaseKeyOf);

            placement = grid.AllAgents.ToDictionary(a => a.Uri, a => a.AssignedNode!.AssignedId);
            overflow.Select(uri => placement[uri]).Distinct().Count().ShouldBe(1, "the partition stays whole");
            hosts.Add(placement[overflow[0]]);
        }

        hosts.ShouldBe([3, 3, 3], $"overflow partition host per evaluation: {string.Join(", ", hosts)}");
    }
}
