using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-4886 follow-up. The fleet behind that report does not sit still: it deploys a new version every few
/// days as a blue/green warm-up, scales up for the warm-up and back down after the cutover, and has lost its
/// leader mid-wave more than once. Every one of those transitions is a different grid for the leader to
/// evaluate, and the existing coverage took each in isolation against a stable node set.
///
/// <para>These drive the leader's real evaluation through whole lifecycles — nodes joining, nodes leaving,
/// the leader being replaced — and assert the invariants that have to hold across <i>every</i> step, not
/// just at the end: no agent is ever started on two nodes, no agent is ever sent to a node that cannot build
/// it, every step converges in a bounded number of rounds, and the leader does not move agents it has no
/// reason to move.</para>
///
/// <para>A few thousand agents rather than the field's sixty thousand: these assert behaviour, and
/// <see cref="fleet_scale_evaluation" /> gates the cost. Everything is seeded and deterministic.</para>
/// </summary>
public class fleet_lifecycle_invariants
{
    private const string Scheme = "fake";

    // 32 shard databases, 10 tenants, 4 projections: 1,280 agents per version
    private static readonly string[] Databases = Enumerable.Range(1, 32).Select(i => $"db{i:D2}").ToArray();
    private static readonly string[] Tenants = Enumerable.Range(1, 10).Select(i => $"t{i}").ToArray();
    private static readonly string[] Projections = ["orders", "invoices", "shipments", "ledger"];

    /// <summary>Only the first projection's version differs between the fleets, as in the field.</summary>
    private static string[] declared(string version) => Databases
        .SelectMany(db => Tenants.SelectMany(t =>
            Projections.Select(p => $"{db}/{p}/{(p == Projections[0] ? version : "v1")}/{t}")))
        .ToArray();

    private static Uri[] uris(IEnumerable<string> names) => names.Select(x => new Uri($"{Scheme}://{x}")).ToArray();

    private static string databaseKey(Uri uri) => uri.Host;

    private static FakeAgentFamily shardedStoreFamily(string[] names) => new(Scheme, names)
    {
        Distribution = grid => grid.DistributeByGroupAffinity(Scheme, databaseKey)
    };

    private static void assertInvariants(SimulatedCluster cluster)
    {
        cluster.DoubleStartReports.ShouldBeEmpty();
        cluster.UndeclaredPlacements.ShouldBeEmpty();
    }

    /// <summary>
    ///     The connection-pool bound group affinity exists for: a database's agents sit on at most one node per
    ///     version that is live, never one per agent.
    /// </summary>
    private static void assertDatabaseAffinity(SimulatedCluster cluster, int liveVersions)
    {
        foreach (var db in Databases)
        {
            var hosts = cluster.RunningAssignments
                .Where(x => x.Key.Host == db)
                .Select(x => x.Value)
                .Distinct()
                .Count();

            hosts.ShouldBeLessThanOrEqualTo(liveVersions, $"{db} is hosted by {hosts} nodes");
        }
    }

    [Fact]
    public async Task a_full_blue_green_deploy_holds_the_invariants_at_every_step()
    {
        var blueNames = declared("v30");
        var greenNames = declared("v31");
        var blue = uris(blueNames);
        var green = uris(greenNames);

        // Three blue nodes, blue leader, settled.
        var cluster = new SimulatedCluster(nodeCount: 3, shardedStoreFamily(blueNames), seed: 4886);
        (await cluster.RunUntilConvergedAsync(maxRounds: 10)).ShouldBeLessThan(10);
        cluster.RunningAgents.Count.ShouldBe(blue.Length);
        assertInvariants(cluster);

        // Green warm-up: two nodes of the new version join. The bumped projection has to start on green and
        // ONLY on green; everything blue is running stays put.
        var green1 = cluster.AddNode(green);
        var green2 = cluster.AddNode(green);
        (await cluster.RunUntilConvergedAsync(maxRounds: 10)).ShouldBeLessThan(10);
        assertInvariants(cluster);
        cluster.RunningAgents.Count.ShouldBe(cluster.AllAgents.Length);
        foreach (var uri in green.Except(blue))
        {
            new[] { green1, green2 }.ShouldContain(cluster.RunningAssignments[uri], $"{uri} is the green version");
        }

        assertDatabaseAffinity(cluster, liveVersions: 2);

        // Cutover: the leader is now a green node, whose own store enumerates only the green version. The
        // blue version keeps running on blue for as long as blue is around.
        green1 = cluster.FailOverLeaderTo(green1, shardedStoreFamily(greenNames));
        (await cluster.RunUntilConvergedAsync(maxRounds: 10)).ShouldBeLessThan(10);
        assertInvariants(cluster);
        cluster.RunningAgents.Count.ShouldBe(cluster.AllAgents.Length);

        // Blue is torn down one node at a time. Each departure takes its agents with it; the blue version's
        // survivors are re-placed on the blue nodes that remain, and once none remain the blue version is
        // simply gone -- no node declares it and the leader's store does not know it.
        var blueNodes = cluster.NodeIds.Where(x => x != green1 && x != green2).ToArray();
        foreach (var blueNode in blueNodes)
        {
            cluster.RemoveNode(blueNode);
            (await cluster.RunUntilConvergedAsync(maxRounds: 10)).ShouldBeLessThan(10);
            assertInvariants(cluster);
            cluster.RunningAgents.Count.ShouldBe(cluster.AllAgents.Length);
        }

        // Steady state on green: exactly the green version, nothing else, one owner per database.
        cluster.RunningAgents.OrderBy(x => x.ToString()).ShouldBe(green.OrderBy(x => x.ToString()));
        assertDatabaseAffinity(cluster, liveVersions: 1);
    }

    /// <summary>
    ///     The report's second symptom: three leaders in a row were replaced during one warm-up. A new leader
    ///     starts with an empty pending-assignment ledger and no memory of what its predecessor dispatched,
    ///     while slow starts are still in flight on every node. It must finish the wave, not restart it.
    /// </summary>
    [Fact]
    public async Task a_leader_replaced_mid_wave_finishes_the_wave_without_restarting_it()
    {
        var cluster = new SimulatedCluster(nodeCount: 5, agentCount: 1280, seed: 4886);

        // A long-tailed start cost, so plenty of starts are in flight when the leader goes.
        var random = new Random(4886);
        var costs = cluster.AllAgents.ToDictionary(x => x, _ => random.Next(1, 12));
        cluster.StartCost = uri => costs[uri];

        for (var i = 0; i < 3; i++) await cluster.RunRoundAsync();

        var dispatchedBeforeFailover = cluster.DispatchCounts.Count;
        dispatchedBeforeFailover.ShouldBe(1280, "the first leader dispatched everything before it was replaced");

        cluster.FailOverLeaderTo(cluster.NodeIdAt(2));

        var rounds = await cluster.RunUntilConvergedAsync(maxRounds: 40);
        rounds.ShouldBeLessThan(40);
        cluster.RunningAgents.Count.ShouldBe(1280);

        // The new leader could not see the in-flight starts, so it re-decided them -- but to the SAME nodes,
        // because the distribution is deterministic over the same node set, so nothing ran twice and nothing
        // was moved.
        cluster.DoubleStartReports.ShouldBeEmpty();
        cluster.StopsEmitted.ShouldBe(0);
        cluster.ReassignmentsEmitted.ShouldBe(0);

        var counts = cluster.RunningCountsByNode;
        counts.Max().ShouldBeLessThanOrEqualTo(counts.Min() + 1);
    }

    /// <summary>
    ///     Scale-down after the warm-up: two nodes leave at once with everything they were running. Each lost
    ///     agent is placed exactly once more, and nothing that survived is touched.
    /// </summary>
    [Fact]
    public async Task losing_two_nodes_re_places_each_lost_agent_exactly_once_and_moves_nothing_else()
    {
        var names = declared("v30");
        var cluster = new SimulatedCluster(nodeCount: 5, shardedStoreFamily(names), seed: 4886);
        (await cluster.RunUntilConvergedAsync(maxRounds: 10)).ShouldBeLessThan(10);

        var leaving = new[] { cluster.NodeIdAt(3), cluster.NodeIdAt(4) };
        var lost = cluster.RunningAssignments.Where(x => leaving.Contains(x.Value)).Select(x => x.Key).ToHashSet();
        lost.ShouldNotBeEmpty();

        foreach (var node in leaving) cluster.RemoveNode(node);

        (await cluster.RunUntilConvergedAsync(maxRounds: 10)).ShouldBeLessThan(10);
        cluster.RunningAgents.Count.ShouldBe(names.Length);
        assertInvariants(cluster);

        foreach (var (uri, dispatches) in cluster.DispatchCounts)
        {
            dispatches.ShouldBe(lost.Contains(uri) ? 2 : 1, uri.ToString());
        }

        cluster.StopsEmitted.ShouldBe(0);
        cluster.ReassignmentsEmitted.ShouldBe(0);
        assertDatabaseAffinity(cluster, liveVersions: 1);
    }

    /// <summary>
    ///     Up and down, repeatedly. A scale-up is allowed to move agents -- that is what filling the new nodes
    ///     means -- but no more than the new nodes' share, and a scale-down moves nothing. Across several cycles
    ///     the cluster must come back to the same steady state every time rather than accumulating churn.
    /// </summary>
    [Fact]
    public async Task repeated_scale_up_and_down_cycles_converge_every_time_with_bounded_movement()
    {
        var names = declared("v30");
        var all = uris(names);
        var cluster = new SimulatedCluster(nodeCount: 3, shardedStoreFamily(names), seed: 4886);
        (await cluster.RunUntilConvergedAsync(maxRounds: 10)).ShouldBeLessThan(10);

        for (var cycle = 1; cycle <= 3; cycle++)
        {
            var movesBefore = cluster.ReassignmentsEmitted;

            var added = new[] { cluster.AddNode(all), cluster.AddNode(all) };
            (await cluster.RunUntilConvergedAsync(maxRounds: 10)).ShouldBeLessThan(10, $"scale-up, cycle {cycle}");
            assertInvariants(cluster);
            cluster.RunningAgents.Count.ShouldBe(all.Length);

            // Filling two of five nodes needs two fifths of the agents to move; groups are indivisible, so
            // allow a database's worth of slack per new node.
            var share = all.Length * added.Length / cluster.NodeIds.Count;
            var groupSize = Tenants.Length * Projections.Length;
            (cluster.ReassignmentsEmitted - movesBefore).ShouldBeLessThanOrEqualTo(share + groupSize * added.Length,
                $"scale-up moved more than the new nodes' share in cycle {cycle}");

            var movesAfterScaleUp = cluster.ReassignmentsEmitted;

            foreach (var node in added) cluster.RemoveNode(node);
            (await cluster.RunUntilConvergedAsync(maxRounds: 10)).ShouldBeLessThan(10, $"scale-down, cycle {cycle}");
            assertInvariants(cluster);
            cluster.RunningAgents.Count.ShouldBe(all.Length);

            cluster.ReassignmentsEmitted.ShouldBe(movesAfterScaleUp, $"scale-down moved survivors in cycle {cycle}");
            cluster.StopsEmitted.ShouldBe(0);
            assertDatabaseAffinity(cluster, liveVersions: 1);
        }
    }
}
