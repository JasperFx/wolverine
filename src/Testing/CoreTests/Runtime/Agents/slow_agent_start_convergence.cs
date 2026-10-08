using JasperFx.Core;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-3779. Every defect in the GH-3753 chain — GH-3748, GH-3749, GH-3750, jasperfx#594, jasperfx#598 —
/// takes a <b>slow agent start</b> as its precondition, and none of them had a dev-scale reproduction:
/// <c>FakeAgent.StartAsync</c> returned <c>Task.CompletedTask</c>, which is precisely the assumption the
/// whole chain violates. The only verification site was a customer's restored-production canary (512 tenant
/// databases, ~6,500 agents, 5 nodes), so every candidate fix shipped as a pinned prerelease and was measured
/// by someone else, and three closed defects carried no regression coverage at all.
///
/// <para>This drives the leader's real <see cref="NodeAgentController.EvaluateAssignmentsAsync" /> round after
/// round against a simulated cluster whose agent starts cost <i>time</i>, and asserts the emergent properties
/// the field reported losing.</para>
///
/// <para><b>Time is measured in evaluation rounds, not milliseconds.</b> The field distribution is p50 27s /
/// p95 82s / a 215s tail against a ~5s health-check cadence, so a wall-clock simulation would take hours and
/// would trade a deterministic assertion for a timing race. A round is one health-check tick; the costs below
/// are that distribution divided by it. Everything here is seeded and deterministic — a failure reproduces.</para>
///
/// <para><b>What this is not.</b> A simulation is not the canary. The pathologies in GH-3753 are emergent from
/// real database contention, real node-table timing and real chunk sizes, so this reproduces the
/// <i>shape</i> of the problem and not its magnitude. Its value is the other direction: a bad fix can be
/// rejected in milliseconds instead of round-tripping through someone else's production restore.</para>
/// </summary>
public class slow_agent_start_convergence
{
    /// <summary>
    /// Start cost in rounds, calibrated against the field distribution behind GH-3753 at a ~5s health-check
    /// cadence: p50 27s (~5 rounds), p95 82s (~16 rounds), tail 215s (~43 rounds). Deliberately a long tail
    /// rather than a uniform cost — a uniformly slow family converges late but converges, while it is the
    /// handful of very slow starts that outlive a reply window and get re-decided.
    /// </summary>
    private static Func<Uri, int> fieldStartCost(int seed, IReadOnlyList<Uri> agents)
    {
        var random = new Random(seed);
        var costs = agents.ToDictionary(x => x, _ =>
        {
            var roll = random.NextDouble();
            if (roll < 0.50) return random.Next(1, 6);
            if (roll < 0.95) return random.Next(6, 17);
            return random.Next(17, 44);
        });

        return uri => costs.TryGetValue(uri, out var cost) ? cost : 1;
    }

    [Fact]
    public async Task every_agent_converges_despite_a_long_tailed_start_distribution()
    {
        var cluster = new SimulatedCluster(nodeCount: 5, agentCount: 603, seed: 3753);
        cluster.StartCost = fieldStartCost(3753, cluster.AllAgents);

        // The slowest single start is 43 rounds; anything much past that is the leader failing to converge
        // rather than the cluster merely being slow.
        var rounds = await cluster.RunUntilConvergedAsync(maxRounds: 60);

        cluster.RunningAgents.Count.ShouldBe(cluster.AllAgents.Length);
        rounds.ShouldBeLessThan(60);
    }

    /// <summary>
    /// GH-3698's failure mode. A dispatched-but-unstarted agent has no persisted assignment row, so it looks
    /// completely unplaced to the next evaluation; without the pending-assignment ledger the leader re-decides
    /// its placement from scratch and sends the same agent to a SECOND node with no stop for the copy already
    /// coming up on the first. Two live copies of a projection agent is the worst outcome in the whole chain.
    /// </summary>
    [Fact]
    public async Task an_agent_is_never_started_on_two_nodes_at_once()
    {
        var cluster = new SimulatedCluster(nodeCount: 5, agentCount: 603, seed: 3753);
        cluster.StartCost = fieldStartCost(3753, cluster.AllAgents);

        await cluster.RunUntilConvergedAsync(maxRounds: 60);

        // The cluster asserts the single-copy invariant on every mutation as it runs; this is the end-state
        // restatement of it.
        cluster.DoubleStartReports.ShouldBeEmpty();
    }

    /// <summary>
    /// The other field shape: five nodes holding 799 / 0 / 0 / 0 / 1. A slow wave must not leave the
    /// distribution lopsided once it has converged.
    /// </summary>
    [Fact]
    public async Task no_node_is_starved_while_another_holds_everything()
    {
        var cluster = new SimulatedCluster(nodeCount: 5, agentCount: 603, seed: 3753);
        cluster.StartCost = fieldStartCost(3753, cluster.AllAgents);

        await cluster.RunUntilConvergedAsync(maxRounds: 60);

        var counts = cluster.RunningCountsByNode;

        // 603 across 5 nodes is 120 with a remainder of 3, so an even spread is 120 or 121 everywhere.
        counts.Max().ShouldBeLessThanOrEqualTo(counts.Min() + 1);
    }

    /// <summary>
    /// A wave of slow starts must not itself generate churn. Once the leader has placed an agent it should
    /// stay placed: every stop or reassignment emitted during a first-time placement wave against a stable
    /// cluster is work the leader is undoing, and it is what turned the field's convergence into a livelock.
    /// </summary>
    [Fact]
    public async Task a_first_placement_wave_emits_no_stops_and_no_reassignments()
    {
        var cluster = new SimulatedCluster(nodeCount: 5, agentCount: 603, seed: 3753);
        cluster.StartCost = fieldStartCost(3753, cluster.AllAgents);

        await cluster.RunUntilConvergedAsync(maxRounds: 60);

        cluster.StopsEmitted.ShouldBe(0);
        cluster.ReassignmentsEmitted.ShouldBe(0);

        // And each agent was asked to start exactly once — no duplicate dispatch of work already in flight.
        cluster.DispatchCounts.Values.ShouldAllBe(x => x == 1);

        // The clearest signal from the field that something was wrong: over one four-minute window the
        // cluster's total running-agent count went 2,401 -> 1,885. Against a stable node set that number can
        // only fall if agents that had come up were stopped, so this is a restatement of the two assertions
        // above rather than an independent one — it is here because it is the shape an operator actually
        // sees, and because it is the assertion that still holds if a node-churn scenario is added later.
        cluster.RunningCountByRound.ShouldBe(cluster.RunningCountByRound.OrderBy(x => x).ToArray());
    }

    /// <summary>
    /// GH-3698, sharpened. The pending-assignment ledger has two ways to hold an agent: a TTL backstop of
    /// <c>2 x CheckAssignmentPeriod</c>, and the dispatcher's own answer to "is this start still outstanding?".
    /// A start slower than the TTL is the normal field case (27s p50 against a 60s TTL is close; the 215s tail
    /// is not close at all), so the TTL is squeezed to nothing here and the probe is left as the only thing
    /// holding the agent. It must be enough on its own.
    /// </summary>
    [Fact]
    public async Task a_start_slower_than_the_ledger_ttl_is_still_held_by_the_outstanding_dispatch()
    {
        var cluster = new SimulatedCluster(nodeCount: 3, agentCount: 12, seed: 3698);

        // 1ms period => a 2ms TTL. Every round below sleeps far past it, so nothing is held by the clock.
        cluster.Options.Durability.CheckAssignmentPeriod = 1.Milliseconds();

        // No start ever lands during this test.
        cluster.StartCost = _ => 1000;

        var first = await cluster.RunRoundAsync();
        cluster.AssignedIn(first).Count().ShouldBe(12);

        for (var i = 0; i < 4; i++)
        {
            // Comfortably past the 2ms TTL, so a re-emission here can only come from the ledger having
            // released an agent whose start is still outstanding.
            await Task.Delay(25.Milliseconds(), TestContext.Current.CancellationToken);

            var round = await cluster.RunRoundAsync();
            round.ShouldBeEmpty();
        }

        cluster.DoubleStartReports.ShouldBeEmpty();
        cluster.DispatchCounts.Values.ShouldAllBe(x => x == 1);
    }

    /// <summary>
    /// GH-3750: a PARTIAL confirmation is the normal case whenever starts are slow — <c>StartAgents</c> only
    /// bags an agent once its start returns, so a chunk of 50 agents whose costs straddle the reply window
    /// answers with a subset. The remainder must still be driven to completion rather than being counted as
    /// started and forgotten.
    /// </summary>
    [Fact]
    public async Task the_unconfirmed_remainder_of_a_partially_started_chunk_still_converges()
    {
        var cluster = new SimulatedCluster(nodeCount: 2, agentCount: 100, seed: 3750);

        // Half the chunk lands almost immediately, half of it long after any reply window — the straddle
        // that produces a partial AgentsStarted reply.
        var slow = cluster.AllAgents.Where((_, i) => i % 2 == 1).ToHashSet();
        cluster.StartCost = uri => slow.Contains(uri) ? 30 : 1;

        var rounds = await cluster.RunUntilConvergedAsync(maxRounds: 50);

        cluster.RunningAgents.Count.ShouldBe(100);
        rounds.ShouldBeLessThan(50);

        // The fast half must not have dragged the slow half into being re-placed somewhere else.
        cluster.DoubleStartReports.ShouldBeEmpty();
        cluster.StopsEmitted.ShouldBe(0);
    }

    /// <summary>
    /// GH-3753 is not about slow starts in general — production converges fine without a version bump. It is
    /// about a deploy that CONTAINS one, which means slow starts and a blue/green capability split at the same
    /// time. Until GH-3792 those two conditions were each covered by tests that passed while their
    /// intersection failed, so this drives the field's actual deploy shape end to end: a sharded store's
    /// grouped agents (DistributeByGroupAffinity, as EventSubscriptionAgentFamily uses for a multi-database
    /// store), a green fleet that alone declares the bumped version, a blue leader whose own family cannot even
    /// enumerate the green agents — and the long-tailed start costs on top.
    /// </summary>
    [Fact]
    public async Task a_version_bump_converges_with_slow_starts_and_no_cross_fleet_placement()
    {
        var databases = Enumerable.Range(1, 12).Select(i => $"db{i:D2}").ToArray();
        var tenants = new[] { "t1", "t2", "t3" };

        string[] Names(string kind) =>
            databases.SelectMany(db => tenants.Select(t => $"{db}/{kind}/{t}")).ToArray();

        var previous = Names("v22");   // built only by the blue fleet
        var bumped = Names("v23");     // built only by the green fleet
        var unchanged = Names("same"); // the projections whose version did not change — built by everyone

        Uri[] Uris(IEnumerable<string> names) => names.Select(x => new Uri($"fake://{x}")).ToArray();

        // The leader is blue: exactly as in production, its own family enumerates only what its store
        // registers, so the green agents reach the grid solely through the green nodes' capabilities.
        var family = new FakeAgentFamily("fake", previous.Concat(unchanged).ToArray())
        {
            // What EventSubscriptionAgentFamily.EvaluateAssignmentsAsync does for a multi-database store:
            // group affinity keyed on the database segment.
            Distribution = grid => grid.DistributeByGroupAffinity("fake", uri => uri.Host)
        };

        var blueCapabilities = Uris(previous.Concat(unchanged));
        var greenCapabilities = Uris(bumped.Concat(unchanged));

        // Nodes 0-1 blue (node 0 is the leader), nodes 2-3 green.
        var cluster = new SimulatedCluster(nodeCount: 4, family, seed: 3753,
            capabilitiesFor: i => i < 2 ? blueCapabilities : greenCapabilities);
        cluster.StartCost = fieldStartCost(3753, cluster.AllAgents);

        var rounds = await cluster.RunUntilConvergedAsync(maxRounds: 60);

        // Every agent of BOTH fleets is running — the bug behind GH-3792 made this impossible: the bumped
        // agents were assigned to blue nodes that cannot build them and the new version never started.
        cluster.RunningAgents.Count.ShouldBe(cluster.AllAgents.Length);
        rounds.ShouldBeLessThan(60);

        // No agent ever landed on a fleet that cannot build it, at any point during the wave.
        var blues = new[] { cluster.NodeIdAt(0), cluster.NodeIdAt(1) };
        var greens = new[] { cluster.NodeIdAt(2), cluster.NodeIdAt(3) };

        foreach (var uri in Uris(previous))
        {
            blues.ShouldContain(cluster.RunningAssignments[uri], $"{uri} is the blue fleet's version");
        }

        foreach (var uri in Uris(bumped))
        {
            greens.ShouldContain(cluster.RunningAssignments[uri], $"{uri} is the green fleet's version");
        }

        // The slow wave generated no churn: nothing was stopped or re-decided while starts were in flight.
        cluster.DoubleStartReports.ShouldBeEmpty();
        cluster.StopsEmitted.ShouldBe(0);
        cluster.ReassignmentsEmitted.ShouldBe(0);

        // And the connection-pool bound that group affinity exists for held through the whole rollout shape:
        // a database's agents sit on at most two nodes — one per version — not one per agent.
        foreach (var db in databases)
        {
            var hosts = tenants
                .SelectMany(t => new[] { $"{db}/v22/{t}", $"{db}/v23/{t}", $"{db}/same/{t}" })
                .Select(name => cluster.RunningAssignments[new Uri($"fake://{name}")])
                .Distinct()
                .ToList();

            hosts.Count.ShouldBeLessThanOrEqualTo(2,
                $"{db} is hosted by {hosts.Count} nodes — a version bump must cost one owner per version, not a pool set per partition");
        }
    }
}
