using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-4901. A blue/green warm-up in which the green nodes register a few evaluations apart. The first green node
/// is the only home for the bumped projections when the leader first sees it, so every green-only agent is queued
/// for it in one evaluation, as start batches in its dispatcher lane. In the field the two green nodes that joined
/// seventeen seconds later got nothing for half an hour while the first crept toward its memory limit.
///
/// <para>Three things kept it that way, and each scenario here fails without the fix for the ones it reaches:</para>
/// <list type="number">
/// <item>The leader projected its pending-start ledger onto the grid before EVERY family's pass, not just the
/// family it belonged to, so the passes after the event-subscription family put each still-queued agent back on
/// the first green node and the decision to move it never left the leader.</item>
/// <item>A move of an agent whose start was still queued ran in the source node's lane, behind that node's whole
/// backlog, so even an emitted move reached the new node only after the old one had started the agent itself.</item>
/// <item>A partition with no sibling host to follow fell back to the lowest load band with no per-node ceiling, so
/// when the first green node reported less load than the newcomers it was handed everything again.</item>
/// </list>
///
/// <para>Fixing the first two lets the leader move agents that are still on their way somewhere, which opens a
/// race of its own: a second move of an agent whose first move has not landed. The one-evaluation-apart scenario
/// covers that.</para>
/// </summary>
public class green_warm_up_spreads_over_every_green_node_4901
{
    private const string Scheme = "fake";

    // 32 shard databases, 10 tenants, 4 projections of which green bumps two: the green-only agents are a third of
    // the cluster's agents once green is up, roughly the field's proportion.
    private static readonly string[] Databases = Enumerable.Range(1, 32).Select(i => $"db{i:D2}").ToArray();
    private static readonly string[] Tenants = Enumerable.Range(1, 10).Select(i => $"t{i}").ToArray();
    private static readonly string[] Projections = ["orders", "invoices", "shipments", "ledger"];
    private static readonly string[] Bumped = ["orders", "invoices"];

    private static string[] declared(string version) => Databases
        .SelectMany(db => Tenants.SelectMany(t =>
            Projections.Select(p => $"{db}/{p}/{(Bumped.Contains(p) ? version : "v1")}/{t}")))
        .ToArray();

    private static Uri[] uris(IEnumerable<string> names) => names.Select(x => new Uri($"{Scheme}://{x}")).ToArray();

    private static FakeAgentFamily shardedStoreFamily(string[] names) => new(Scheme, names)
    {
        Distribution = grid => grid.DistributeByGroupAffinity(Scheme, uri => uri.Host)
    };

    [Fact]
    public Task green_only_agents_reach_every_green_node_that_joins()
        => warmUpAsync(firstGreenLoad: null, laterGreenLoad: null, roundsBetweenJoins: 3);

    /// <summary>
    ///     The third node joins while the moves to the second are still on their way, and with large batches
    ///     some of them were already being started on the first. Re-deciding those as a second move from the
    ///     first node queues it behind the first move, which finds the agent already gone and starts it twice.
    /// </summary>
    [Fact]
    public Task green_nodes_joining_one_evaluation_apart_never_start_an_agent_twice()
        => warmUpAsync(firstGreenLoad: null, laterGreenLoad: null, roundsBetweenJoins: 1, startBatchSize: 250);

    /// <summary>
    ///     Capacity-aware assignment on, and the first green node reporting less memory pressure than the ones
    ///     that join after it -- a pod that has just started reads high before its first collections.
    /// </summary>
    [Fact]
    public Task green_only_agents_reach_every_green_node_when_the_first_reports_the_lightest_load()
        => warmUpAsync(firstGreenLoad: 25, laterGreenLoad: 45, roundsBetweenJoins: 3);

    private static async Task warmUpAsync(double? firstGreenLoad, double? laterGreenLoad, int roundsBetweenJoins,
        int? startBatchSize = null)
    {
        var blueNames = declared("v30");
        var blue = uris(blueNames);
        var green = uris(declared("v31"));
        var greenOnly = green.Except(blue).ToHashSet();

        await using var cluster = new SimulatedCluster(nodeCount: 5, shardedStoreFamily(blueNames), seed: 4901);
        cluster.UseDispatcherLanes();
        if (startBatchSize.HasValue)
        {
            cluster.Options.Durability.AgentStartBatchSize = startBatchSize.Value;
            cluster.Options.Durability.MaxAgentStartParallelism = startBatchSize.Value;
        }

        if (firstGreenLoad.HasValue)
        {
            cluster.Options.Durability.CapacityAwareAssignment = true;
            cluster.Options.Durability.NodeOverloadThreshold = 70;
            foreach (var node in cluster.NodeIds) cluster.AdvertiseLoad(node, 35);
        }

        cluster.StartCost = _ => 1;
        (await cluster.RunUntilConvergedAsync(maxRounds: 100)).ShouldBeLessThan(100, "blue settles");

        // A projection behind a version bump replays inside its start, so each start takes a while and a node
        // works through its batches MaxAgentStartParallelism agents at a time.
        cluster.StartCost = _ => 3;

        var greenNodes = new List<Guid>();
        var peak = new Dictionary<Guid, int>();

        int greenOnlyOn(Guid node) =>
            cluster.RunningAssignments.Count(x => x.Value == node && greenOnly.Contains(x.Key));

        async Task runRoundsAsync(int rounds)
        {
            for (var i = 0; i < rounds; i++)
            {
                await cluster.RunRoundAsync();
                foreach (var node in cluster.NodeIds)
                {
                    peak[node] = Math.Max(peak.GetValueOrDefault(node), cluster.RunningCountOn(node));
                }
            }
        }

        void join(double? load)
        {
            var node = cluster.AddNode(green);
            if (load.HasValue) cluster.AdvertiseLoad(node, load);
            greenNodes.Add(node);
        }

        // The first green node registers alone; the other two follow a few evaluations apart, as the pods of one
        // rollout do.
        join(firstGreenLoad);
        await runRoundsAsync(3);
        join(laterGreenLoad);
        await runRoundsAsync(roundsBetweenJoins);
        join(laterGreenLoad);

        // A start costs three rounds, so a node sent work right away is running some of it within a handful of
        // rounds -- not hundreds of rounds later, once the first node has worked through its backlog.
        await runRoundsAsync(8);
        foreach (var node in greenNodes)
        {
            greenOnlyOn(node).ShouldBeGreaterThan(0,
                $"green node {greenNodes.IndexOf(node) + 1} is not running any green-only agent yet");
        }

        (await cluster.RunUntilConvergedAsync(maxRounds: 150)).ShouldBeLessThan(150, "the warm-up converges");
        cluster.RunningAgents.Count.ShouldBe(cluster.AllAgents.Length);

        foreach (var node in greenNodes)
        {
            greenOnlyOn(node).ShouldBeGreaterThanOrEqualTo(greenOnly.Count / greenNodes.Count / 2,
                $"green node {greenNodes.IndexOf(node) + 1} ended up with too little of the bumped projections");
        }

        // No node ever ran more than its share, give or take one shard database: groups are indivisible.
        var share = (int)Math.Ceiling((double)cluster.AllAgents.Length / cluster.NodeIds.Count);
        var groupSize = Tenants.Length * Projections.Length;
        foreach (var (node, count) in peak)
        {
            count.ShouldBeLessThanOrEqualTo(share + groupSize, $"node {node} peaked above its share");
        }

        foreach (var uri in greenOnly)
        {
            greenNodes.ShouldContain(cluster.RunningAssignments[uri], $"{uri} is the green version");
        }

        cluster.DoubleStartReports.ShouldBeEmpty();
        cluster.UndeclaredPlacements.ShouldBeEmpty();
        cluster.DuplicateCopies.ShouldBe(0);
        cluster.LeaderErrors.ShouldBeEmpty();
    }
}
