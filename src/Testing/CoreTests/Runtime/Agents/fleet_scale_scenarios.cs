using JasperFx.Core;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// The production events behind GH-4886, GH-4901 and GH-3959, replayed through <see cref="SimulatedCluster" /> with
/// real dispatcher lanes, at the field's shape: a sharded store, per-tenant projection agents grouped per shard
/// database, five blue nodes and three green warm-up nodes whose version bumps most projections.
///
/// <para>Every scenario holds the same invariants: no agent is started on two nodes, nothing is sent to a node
/// that does not declare it, each step converges within a bounded number of rounds, nodes with the same
/// capabilities end up balanced, no node takes on more than it ends up with plus one shard database on the way,
/// and no single evaluation exceeds a wall-time budget -- loose, there to make a quadratic regression like
/// GH-4886 fail instead of hang.</para>
///
/// <para>Each runs in the <c>ci</c> shape on every build. The <c>production</c> shape (512 databases, about
/// 100,000 agents) is opt-in: set <c>WOLVERINE_FLEET_SCALE=production</c>. A leader evaluation at that size costs
/// a few seconds, so those runs take minutes.</para>
/// </summary>
public class fleet_scale_scenarios
{
    private const string Scheme = "fake";

    internal sealed record Fleet(
        string Name,
        int Databases,
        int Tenants,
        int Projections,
        int Bumped,
        int StartBatchSize,
        TimeSpan EvaluationBudget)
    {
        // Tenants are spread over the databases round-robin, so a database holds at most this many agents
        public int GroupSize => (int)Math.Ceiling((double)Tenants / Databases) * Projections;

        public string[] Declared(string version) => Enumerable.Range(1, Tenants)
            .SelectMany(t => Enumerable.Range(1, Projections).Select(p =>
                $"shard-{t % Databases + 1:D3}/p{p:D2}/{(p <= Bumped ? version : "v1")}/{1_050_000 + t}"))
            .ToArray();
    }

    // 3,072 agents per version, 2,048 of them bumped
    private static readonly Fleet Ci = new("ci", 64, 256, 12, 8, 250, 5.Seconds());

    // 59,800 agents per version, 41,600 of them bumped: 101,400 in the grid during a warm-up
    private static readonly Fleet Production = new("production", 512, 2600, 23, 16, 2500, 20.Seconds());

    public static TheoryData<string> Shapes => ["ci", "production"];

    private static Fleet fleetFor(string shape)
    {
        if (shape == Ci.Name) return Ci;

        if (Environment.GetEnvironmentVariable("WOLVERINE_FLEET_SCALE") != Production.Name)
        {
            Assert.Skip("Production-sized fleet: set WOLVERINE_FLEET_SCALE=production to run it");
        }

        return Production;
    }

    private static Uri[] uris(IEnumerable<string> names) => names.Select(x => new Uri($"{Scheme}://{x}")).ToArray();

    private static FakeAgentFamily familyFor(string[] names) => new(Scheme, names)
    {
        Distribution = grid => grid.DistributeByGroupAffinity(Scheme, uri => uri.Host)
    };

    private static SimulatedCluster blueFleet(Fleet fleet, int seed, string[]? blueNames = null)
    {
        var cluster = new SimulatedCluster(nodeCount: 5, familyFor(blueNames ?? fleet.Declared("v30")), seed);
        cluster.UseDispatcherLanes();

        // A fleet this size runs with these raised well above the defaults, as the field does
        cluster.Options.Durability.AgentStartBatchSize = fleet.StartBatchSize;
        cluster.Options.Durability.MaxAgentStartParallelism = fleet.StartBatchSize;

        return cluster;
    }

    /// <summary>
    ///     Tracks every node's peak running count from a starting point, so a step can be held to "no node took
    ///     on more than it ends up with, plus one shard database".
    /// </summary>
    private sealed class Watch
    {
        private readonly SimulatedCluster _cluster;
        private readonly Dictionary<Guid, int> _before = new();
        private readonly Dictionary<Guid, int> _peak = new();

        public Watch(SimulatedCluster cluster)
        {
            _cluster = cluster;
            cluster.AfterRound = _ => sample();
        }

        public void Begin()
        {
            _before.Clear();
            _peak.Clear();
            foreach (var node in _cluster.NodeIds) _before[node] = _cluster.RunningCountOn(node);
        }

        private void sample()
        {
            foreach (var (_, node) in _cluster.RunningAssignments.GroupBy(x => x.Value).Select(x => (x.Count(), x.Key)))
            {
                var count = _cluster.RunningCountOn(node);
                _peak[node] = Math.Max(_peak.GetValueOrDefault(node), count);
            }
        }

        public void AssertNoPileUp(int slack, string step)
        {
            foreach (var node in _cluster.NodeIds)
            {
                var limit = Math.Max(_before.GetValueOrDefault(node), _cluster.RunningCountOn(node)) + slack;
                _peak.GetValueOrDefault(node).ShouldBeLessThanOrEqualTo(limit,
                    $"{step}: node {node} peaked at {_peak.GetValueOrDefault(node)}");
            }
        }
    }

    private static async Task convergeAsync(SimulatedCluster cluster, Fleet fleet, string step, int maxRounds = 40)
    {
        AgentCommands? last = null;
        var hook = cluster.AfterRound;
        cluster.AfterRound = commands =>
        {
            last = commands;
            hook?.Invoke(commands);
        };

        var rounds = await cluster.RunUntilConvergedAsync(maxRounds);
        cluster.AfterRound = hook;
        rounds.ShouldBeLessThan(maxRounds, $"{step} did not converge: {cluster.Describe(last)}");
        cluster.RunningAgents.Count.ShouldBe(cluster.AllAgents.Length, step);
        assertInvariants(cluster, fleet, step);
        assertBalanced(cluster, fleet, step);
    }

    private static void assertInvariants(SimulatedCluster cluster, Fleet fleet, string step)
    {
        cluster.RestartsWhileStarting.ShouldBeEmpty(step);
        cluster.DoubleStartReports.ShouldBeEmpty(step);
        cluster.UndeclaredPlacements.ShouldBeEmpty(step);
        cluster.DuplicateCopies.ShouldBe(0, step);
        cluster.LeaderErrors.ShouldBeEmpty(step);
        cluster.EvaluationTimes.Max().ShouldBeLessThan(fleet.EvaluationBudget, $"{step}: slowest evaluation");
    }

    /// <summary>
    ///     When every node declares the same agents, they are within two shard databases of each other. While two
    ///     versions are live the leader deliberately leaves a node's agents alone up to the cluster-wide ceiling,
    ///     so nodes of one version can legitimately differ more; <see cref="Watch" /> covers pile-ups there.
    /// </summary>
    private static void assertBalanced(SimulatedCluster cluster, Fleet fleet, string step)
    {
        var declarations = cluster.Declarations.Select(x => x.Capabilities.ToHashSet()).ToList();
        if (declarations.Skip(1).Any(x => !x.SetEquals(declarations[0]))) return;

        var counts = cluster.RunningCountsByNode;
        (counts.Max() - counts.Min()).ShouldBeLessThanOrEqualTo(2 * fleet.GroupSize,
            $"{step}: the nodes run {string.Join(", ", counts)}");
    }

    /// <summary>
    ///     A deploy, end to end: green warm-up nodes register one by one, warm the bumped projections, the blue
    ///     deployment is rolled onto the new version pod by pod, and the warm-up nodes are taken away.
    /// </summary>
    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task a_full_blue_green_deploy(string shape)
    {
        var fleet = fleetFor(shape);
        var blueNames = fleet.Declared("v30");
        var greenNames = fleet.Declared("v31");
        var blue = uris(blueNames);
        var green = uris(greenNames);
        var greenOnly = green.Except(blue).ToHashSet();

        await using var cluster = blueFleet(fleet, seed: 4901, blueNames);
        var watch = new Watch(cluster);
        await convergeAsync(cluster, fleet, "cold start");

        // Warm-up: three green nodes register a few evaluations apart. Whatever the first one starts while it is
        // alone is progress; from the moment the last one has joined, nobody piles up any more -- give or take
        // the batch a node was already starting when each newcomer took part of its share away.
        var warmUp = new List<Guid> { cluster.AddNode(green) };
        await cluster.RunRoundAsync();
        await cluster.RunRoundAsync();
        warmUp.Add(cluster.AddNode(green));
        await cluster.RunRoundAsync();
        warmUp.Add(cluster.AddNode(green));
        watch.Begin();
        await convergeAsync(cluster, fleet, "warm-up");
        watch.AssertNoPileUp(fleet.GroupSize + 2 * fleet.StartBatchSize, "warm-up");

        foreach (var node in warmUp)
        {
            cluster.RunningAssignments.Count(x => x.Value == node && greenOnly.Contains(x.Key))
                .ShouldBeGreaterThanOrEqualTo(greenOnly.Count / warmUp.Count - 2 * fleet.GroupSize,
                    "each warm-up node carries its share of the bumped projections");
        }

        // Cutover: a green node takes over the leadership, and the blue deployment rolls onto the new version
        // one pod at a time. Each step may overshoot by the batch a node was starting when the plan changed.
        var leader = cluster.FailOverLeaderTo(warmUp[0], familyFor(greenNames));
        warmUp[0] = leader;

        var rolled = new List<Guid>();
        foreach (var bluePod in cluster.NodeIds.Except(warmUp).ToArray())
        {
            watch.Begin();
            cluster.RemoveNode(bluePod);
            rolled.Add(cluster.AddNode(green));
            await convergeAsync(cluster, fleet, $"rolling blue pod {rolled.Count}");
            watch.AssertNoPileUp(fleet.GroupSize + fleet.StartBatchSize, $"rolling blue pod {rolled.Count}");
        }

        // The warm-up nodes leave, the leadership with them
        cluster.FailOverLeaderTo(rolled[0], familyFor(greenNames));
        foreach (var node in warmUp)
        {
            watch.Begin();
            cluster.RemoveNode(node);
            await convergeAsync(cluster, fleet, "removing a warm-up node");
            watch.AssertNoPileUp(fleet.GroupSize + fleet.StartBatchSize, "removing a warm-up node");
        }

        cluster.NodeIds.Count.ShouldBe(5);
        cluster.RunningAgents.OrderBy(x => x.ToString()).ShouldBe(green.OrderBy(x => x.ToString()));
    }

    /// <summary>
    ///     GH-3959's shape during a warm-up: a green node dies while the wave is still landing. What it held has to
    ///     spread over the green nodes left, not pile onto one of them.
    /// </summary>
    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task a_green_node_dying_mid_wave_spreads_its_share_over_the_survivors(string shape)
    {
        var fleet = fleetFor(shape);
        var blueNames = fleet.Declared("v30");
        var green = uris(fleet.Declared("v31"));
        var greenOnly = green.Except(uris(blueNames)).ToHashSet();

        await using var cluster = blueFleet(fleet, seed: 3959, blueNames);
        var watch = new Watch(cluster);
        await convergeAsync(cluster, fleet, "cold start");

        // Slow starts, so the wave is still landing when the node goes
        cluster.StartCost = _ => 3;
        var greenNodes = new[] { cluster.AddNode(green), cluster.AddNode(green), cluster.AddNode(green) };
        for (var i = 0; i < 4; i++) await cluster.RunRoundAsync();
        cluster.RunningAgents.Count.ShouldBeLessThan(cluster.AllAgents.Length, "the wave is still landing");

        watch.Begin();
        cluster.RemoveNode(greenNodes[1]);
        await convergeAsync(cluster, fleet, "after the green node died");
        watch.AssertNoPileUp(fleet.GroupSize, "after the green node died");

        var survivors = new[] { greenNodes[0], greenNodes[2] }
            .Select(node => cluster.RunningAssignments.Count(x => x.Value == node && greenOnly.Contains(x.Key)))
            .ToArray();
        (survivors.Max() - survivors.Min()).ShouldBeLessThanOrEqualTo(2 * fleet.GroupSize,
            $"the survivors carry {string.Join(" and ", survivors)} of the bumped projections");
    }

    /// <summary>
    ///     The leader dies in the middle of a warm-up. What it still had queued in its lanes dies with it; the
    ///     batches it had already handed to nodes keep starting there, invisible to its successor, which has an
    ///     empty ledger and its own dispatcher. A takeover hold at least as long as a start covers them (GH-4897).
    /// </summary>
    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task the_leader_dying_mid_warm_up(string shape)
    {
        var fleet = fleetFor(shape);
        await using var cluster = await leaderDiesMidWarmUpAsync(fleet, holdEvaluations: StartRounds + 1);

        await convergeAsync(cluster, fleet, "after the leader died");
    }

    /// <summary>
    ///     The same with the default one-evaluation hold, which a start lasting several evaluations outlives.
    ///     This used to pin the cost of the default: the successor started some agents a second time and GH-2602
    ///     stopped the older copies once both were visible. Since GH-3987 a partition keeps the node already
    ///     running most of it, so the starts the dead leader left in flight land where the successor places the
    ///     rest of their database and nothing runs twice on this shape. Whatever a future shape costs, the
    ///     invariant is that duplicates never survive convergence.
    /// </summary>
    [Fact]
    public async Task the_leader_dying_mid_warm_up_with_the_default_hold_heals_its_duplicates()
    {
        await using var cluster = await leaderDiesMidWarmUpAsync(Ci, holdEvaluations: null);

        (await cluster.RunUntilConvergedAsync(40)).ShouldBeLessThan(40, cluster.Describe());
        cluster.RunningAgents.Count.ShouldBe(cluster.AllAgents.Length);

        cluster.DuplicateCopies.ShouldBe(0);
        cluster.DoubleStartReports.ShouldBeEmpty(
            "the in-flight starts land on the node the successor keeps their database on, so nothing starts twice");
    }

    private const int StartRounds = 2;

    private static async Task<SimulatedCluster> leaderDiesMidWarmUpAsync(Fleet fleet, int? holdEvaluations)
    {
        var blueNames = fleet.Declared("v30");
        var green = uris(fleet.Declared("v31"));

        var cluster = blueFleet(fleet, seed: 4897, blueNames);
        await convergeAsync(cluster, fleet, "cold start");

        cluster.StartCost = _ => StartRounds;
        cluster.AddNode(green);
        cluster.AddNode(green);
        cluster.AddNode(green);
        for (var i = 0; i < 3; i++) await cluster.RunRoundAsync();
        cluster.InFlightStarts.ShouldBeGreaterThan(0, "the warm-up is under way");

        var dying = cluster.LeaderNodeId;
        cluster.FailOverLeaderTo(cluster.NodeIdAt(1));
        cluster.RemoveNode(dying);

        if (holdEvaluations.HasValue)
        {
            cluster.Options.Durability.LeaderTakeoverHoldEvaluations = holdEvaluations.Value;
        }

        return cluster;
    }

    /// <summary>
    ///     A shard server refusing connections (Postgres 53300, too many clients) while the warm-up runs: every
    ///     start against a few databases fails for a while. The rest of the fleet must not wait for them, and once
    ///     the server recovers the failed agents are placed after all.
    /// </summary>
    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task starts_failing_against_some_databases_hold_up_nothing_else(string shape)
    {
        var fleet = fleetFor(shape);
        var blueNames = fleet.Declared("v30");
        var green = uris(fleet.Declared("v31"));

        await using var cluster = blueFleet(fleet, seed: 53300, blueNames);
        await convergeAsync(cluster, fleet, "cold start");

        // An unconfirmed start is re-driven once the ledger's TTL (2 x CheckAssignmentPeriod) has passed; the
        // simulation's rounds are milliseconds apart, so the TTL has to be too.
        cluster.Options.Durability.CheckAssignmentPeriod = 1.Milliseconds();

        var refusing = Enumerable.Range(1, fleet.Databases / 8).Select(i => $"shard-{i * 8:D3}").ToHashSet();
        var refused = true;
        cluster.StartFails = (uri, _) => refused && refusing.Contains(uri.Host);

        cluster.AddNode(green);
        cluster.AddNode(green);
        cluster.AddNode(green);

        var healthy = cluster.AllAgents.Where(x => !refusing.Contains(x.Host)).ToArray();
        for (var round = 0; round < 15 && healthy.Any(x => !cluster.RunningAssignments.ContainsKey(x)); round++)
        {
            await cluster.RunRoundAsync();
        }

        healthy.ShouldAllBe(x => cluster.RunningAssignments.ContainsKey(x),
            "the healthy databases are placed while the others still refuse");
        cluster.FailedStarts.ShouldBeGreaterThan(0);
        assertInvariants(cluster, fleet, "while refusing");

        refused = false;
        await convergeAsync(cluster, fleet, "after the server recovered");
    }

    /// <summary>
    ///     Slow starts -- a projection replaying behind its version bump takes seconds to minutes -- worked through
    ///     on per-node lanes at the default parallelism. Every capable node has to be starting at once, and a node
    ///     that joins mid-wave has to be given work straight away rather than after the others' backlogs.
    /// </summary>
    [Fact]
    public async Task slow_starts_run_on_every_node_at_once()
    {
        // Small enough to run at the default batch size and parallelism, which is the point
        var fleet = new Fleet("slow", 32, 64, 6, 0, 0, 5.Seconds());
        var names = fleet.Declared("v1");

        await using var cluster = new SimulatedCluster(nodeCount: 3, familyFor(names), seed: 3779);
        cluster.UseDispatcherLanes();

        var random = new Random(3779);
        var costs = uris(names).ToDictionary(x => x, _ => random.Next(1, 13));
        cluster.StartCost = uri => costs[uri];

        var busiest = 0;
        cluster.AfterRound = _ => busiest = Math.Max(busiest, cluster.NodeIds.Count(n => cluster.StartingOn(n) > 0));

        for (var i = 0; i < 5; i++) await cluster.RunRoundAsync();
        busiest.ShouldBe(3, "every node is starting agents at the same time");

        // Two nodes join while the first three are still working through their batches
        var joined = new[] { cluster.AddNode(uris(names)), cluster.AddNode(uris(names)) };
        for (var i = 0; i < 4; i++) await cluster.RunRoundAsync();
        foreach (var node in joined)
        {
            (cluster.StartingOn(node) + cluster.RunningCountOn(node)).ShouldBeGreaterThan(0,
                "a node that joins mid-wave is given work at once");
        }

        // The whole wave, at ten starts per node at a time and an average of six rounds each, is a few batches'
        // worth per node -- not the total serialized through one node.
        var perNode = names.Length / 5;
        var serialized = names.Length / Math.Max(1, cluster.Options.Durability.MaxAgentStartParallelism) * 6;
        var rounds = await cluster.RunUntilConvergedAsync(maxRounds: serialized);
        rounds.ShouldBeLessThan(serialized / 2, $"{names.Length} agents, {perNode} a node");

        assertInvariants(cluster, fleet, "slow starts");
        assertBalanced(cluster, fleet, "slow starts");
    }

    /// <summary>
    ///     Pods of one deployment that captured slightly different tenant sets at startup: two blue nodes started
    ///     after a tenant was added and declare its 18 agents, the others do not. Assignment has to converge, put
    ///     those agents on the nodes that can run them, and keep every other database on one node.
    /// </summary>
    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task blue_nodes_that_captured_different_tenants_still_converge(string shape)
    {
        var fleet = fleetFor(shape);
        var blueNames = fleet.Declared("v30");
        var blue = uris(blueNames);
        var newTenant = Enumerable.Range(1, 18).Select(p => new Uri($"{Scheme}://shard-001/x{p:D2}/v1/9999999")).ToArray();

        await using var cluster = new SimulatedCluster(nodeCount: 5, familyFor(blueNames), seed: 18,
            capabilitiesFor: i => i >= 3 ? blue.Concat(newTenant).ToArray() : blue);
        cluster.UseDispatcherLanes();
        cluster.Options.Durability.AgentStartBatchSize = fleet.StartBatchSize;
        cluster.Options.Durability.MaxAgentStartParallelism = fleet.StartBatchSize;

        await convergeAsync(cluster, fleet, "cold start");

        var capable = new[] { cluster.NodeIdAt(3), cluster.NodeIdAt(4) };
        newTenant.ShouldAllBe(x => capable.Contains(cluster.RunningAssignments[x]));

        foreach (var database in cluster.RunningAssignments.GroupBy(x => x.Key.Host).Where(x => x.Key != "shard-001"))
        {
            database.Select(x => x.Value).Distinct().Count().ShouldBe(1, $"{database.Key} is spread over nodes");
        }

        // Settled: nothing keeps moving
        for (var i = 0; i < 3; i++) (await cluster.RunRoundAsync()).ShouldBeEmpty();
    }
}
