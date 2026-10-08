using JasperFx.Core;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-4886 follow-up. The report behind that PR: 512 sharded databases, per-tenant projection agents, about
/// 62,000 event-subscription agents on 5 to 8 nodes, a blue/green warm-up every deploy. A newly elected leader
/// spent 66 minutes of single-core CPU in its first assignment evaluation, nothing was assigned in the
/// meantime, and three leaders in a row were replaced before one finished.
///
/// <para>The PR fixed the capability match, which was quadratic in the agent count, with a measured 2-3 s for
/// that one distribution call at this scale. These tests gate the <i>whole</i> leader evaluation at the field
/// shape — <see cref="NodeAgentController.EvaluateAssignmentsAsync" /> end to end, including the command
/// batching after the distribution — because the first evaluation of a fresh leader against a cold cluster
/// emits one start command per agent, and every list scan on that path is the same quadratic shape in the
/// same number.</para>
///
/// <para>The ceilings are loose on purpose: they are there so a regression back to minutes fails the run
/// instead of hanging it, not to pin a benchmark. <see cref="capability_matching_at_fleet_scale" /> covers
/// the grid alone; this covers the leader around it.</para>
/// </summary>
public class fleet_scale_evaluation
{
    private const string Scheme = "fake";

    // 64 shard databases, 2,600 tenants, 23 projections: 59,800 agents per version, of which only one
    // projection's version differs between the fleets — 62,400 distinct agents in the grid.
    private static readonly string[] Projections = Enumerable.Range(1, 23).Select(i => $"projection-{i}").ToArray();

    private static readonly (int Shard, int Tenant)[] Tenants =
        Enumerable.Range(1, 2600).Select(i => (Shard: i % 64 + 1, Tenant: 1_050_000 + i)).ToArray();

    private static string agentName(int shard, string projection, int tenant)
        => $"shard-{shard:D3}/{projection}/{tenant:D8}";

    private static string[] declared(string version) => Tenants.SelectMany(t =>
        Projections.Select(p => agentName(t.Shard, $"{p}/{(p == "projection-1" ? version : "v1")}", t.Tenant))).ToArray();

    private static Uri[] uris(IEnumerable<string> names) => names.Select(x => new Uri($"{Scheme}://{x}")).ToArray();

    // What EventSubscriptionAgentFamily.DatabaseKeyOf extracts for a multi-database store: the shard database.
    private static string databaseKey(Uri uri) => uri.Host;

    /// <summary>
    /// The report, as a cold start: a fresh leader on a blue node, every agent unplaced, three blue nodes
    /// declaring the previous version and two green nodes declaring the bumped one. Group affinity, as the
    /// family uses for a sharded store.
    /// </summary>
    private static SimulatedCluster theReportedFleet()
    {
        var blueNames = declared("v30");
        var blue = uris(blueNames);
        var green = uris(declared("v31"));

        // The leader is blue, so its own family enumerates only the blue version; green's agents reach the grid
        // through the green nodes' capabilities.
        var family = new FakeAgentFamily(Scheme, blueNames)
        {
            Distribution = grid => grid.DistributeByGroupAffinity(Scheme, databaseKey)
        };

        return new SimulatedCluster(nodeCount: 5, family, seed: 4886, capabilitiesFor: i => i < 3 ? blue : green);
    }

    [Fact]
    public async Task a_cold_leader_places_sixty_thousand_agents_in_seconds_not_minutes()
    {
        var cluster = theReportedFleet();

        // Everything is declared and nothing is running, so the very first evaluation decides every agent
        // and emits a start for each. That is the evaluation the report measured at 66 minutes.
        var first = await cluster.RunRoundAsync();

        cluster.AssignedIn(first).Count().ShouldBe(cluster.AllAgents.Length);

        cluster.EvaluationTimes[0].ShouldBeLessThan(20.Seconds(),
            $"first evaluation took {cluster.EvaluationTimes[0].TotalSeconds:F1}s");

        // No agent was sent to a fleet that cannot build it.
        cluster.UndeclaredPlacements.ShouldBeEmpty();
    }

    [Fact]
    public async Task the_whole_warm_up_converges_in_a_handful_of_rounds()
    {
        var cluster = theReportedFleet();

        var rounds = await cluster.RunUntilConvergedAsync(maxRounds: 10);

        rounds.ShouldBeLessThan(10);
        cluster.RunningAgents.Count.ShouldBe(cluster.AllAgents.Length);

        cluster.DoubleStartReports.ShouldBeEmpty();
        cluster.UndeclaredPlacements.ShouldBeEmpty();
        cluster.StopsEmitted.ShouldBe(0);
        cluster.ReassignmentsEmitted.ShouldBe(0);

        // A settled cluster's evaluation is the steady-state cost the leader pays every health check, so it
        // must be cheap at this scale even though it still matches every agent to its candidate nodes.
        var settled = cluster.EvaluationTimes[^1];
        settled.ShouldBeLessThan(10.Seconds(), $"steady-state evaluation took {settled.TotalSeconds:F1}s; all rounds: {describe(cluster)}");
    }

    /// <summary>
    /// The same scale through the other blue/green path. A single-database store with per-tenant partitioning
    /// has the same agent count and the same capability split, but the family distributes it with
    /// <see cref="AssignmentGrid.DistributeEvenlyWithBlueGreenSemantics(string)" /> instead of group affinity.
    /// </summary>
    [Fact]
    public async Task the_even_blue_green_path_is_also_seconds_at_this_scale()
    {
        var blueNames = declared("v30");
        var greenNames = declared("v31");
        var blue = uris(blueNames);
        var green = uris(greenNames);

        var family = new FakeAgentFamily(Scheme, blueNames)
        {
            Distribution = grid => grid.DistributeEvenlyWithBlueGreenSemantics(Scheme)
        };

        var cluster = new SimulatedCluster(nodeCount: 5, family, seed: 4886,
            capabilitiesFor: i => i < 3 ? blue : green);

        var rounds = await cluster.RunUntilConvergedAsync(maxRounds: 10);

        rounds.ShouldBeLessThan(10);
        cluster.RunningAgents.Count.ShouldBe(cluster.AllAgents.Length);
        cluster.UndeclaredPlacements.ShouldBeEmpty();
        cluster.DoubleStartReports.ShouldBeEmpty();

        cluster.EvaluationTimes[0].ShouldBeLessThan(20.Seconds(), $"first evaluation: {describe(cluster)}");
        cluster.EvaluationTimes[^1].ShouldBeLessThan(10.Seconds(), $"steady state: {describe(cluster)}");
    }

    private static string describe(SimulatedCluster cluster)
        => string.Join(", ", cluster.EvaluationTimes.Select(x => $"{x.TotalSeconds:F2}s"));
}
