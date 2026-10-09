using JasperFx.Core;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-4894. <see cref="fleet_lifecycle_invariants" /> drives four hand-written transition sequences through the
/// leader's real evaluation. This rolls the sequence instead: every round a seeded random schedule may bring a
/// node up (same version, or a bumped one that starts a blue/green warm-up), take a node down, replace the
/// leader, or kill the leader outright, for as many rounds as asked — and the assignment invariants are
/// checked on every single round, plus full convergence whenever the fleet has been quiet long enough.
///
/// <para><b>Deliberately not part of the CI run.</b> It is a soak test: run it by hand, or on a schedule, with
/// <c>WOLVERINE_CHAOS_SOAK=&lt;rounds&gt;</c> set (and optionally <c>WOLVERINE_CHAOS_SEED</c> to replay one). Without
/// the variable it skips. A failure names its seed and round in the message so it reproduces exactly.</para>
///
/// <para>The fleet is the shape of the GH-4886 report, at a few thousand agents: a sharded store with
/// per-tenant projection agents distributed by group affinity, where a version bump changes one projection's
/// agents and every node of a version declares exactly that version's agents.</para>
/// </summary>
public class fleet_chaos_soak
{
    private const string Scheme = "fake";
    private const int MaxNodes = 8;
    private const int MinNodes = 2;

    /// <summary>Starts cost one to three rounds, so a quiet window of this many rounds must see convergence.</summary>
    private const int QuietRoundsToConverge = 20;

    private static readonly string[] Databases = Enumerable.Range(1, 24).Select(i => $"db{i:D2}").ToArray();
    private static readonly string[] Tenants = Enumerable.Range(1, 8).Select(i => $"t{i}").ToArray();
    private static readonly string[] Projections = ["orders", "invoices", "shipments"];

    private static string[] declared(int version) => Databases
        .SelectMany(db => Tenants.SelectMany(t =>
            Projections.Select(p => $"{db}/{p}/{(p == Projections[0] ? $"v{version}" : "v1")}/{t}")))
        .ToArray();

    private static Uri[] uris(IEnumerable<string> names) => names.Select(x => new Uri($"{Scheme}://{x}")).ToArray();

    private static FakeAgentFamily familyFor(int version) => new(Scheme, declared(version))
    {
        Distribution = grid => grid.DistributeByGroupAffinity(Scheme, uri => uri.Host)
    };

    [Fact]
    public async Task random_transitions_never_break_the_assignment_invariants()
    {
        var configured = Environment.GetEnvironmentVariable("WOLVERINE_CHAOS_SOAK");
        if (!int.TryParse(configured, out var rounds) || rounds < 1)
        {
            Assert.Skip("Soak test: set WOLVERINE_CHAOS_SOAK=<rounds> (and optionally WOLVERINE_CHAOS_SEED=<seed>) to run it");
        }

        var seed = int.TryParse(Environment.GetEnvironmentVariable("WOLVERINE_CHAOS_SEED"), out var s)
            ? s
            : Random.Shared.Next();

        await runAsync(seed, rounds);
    }

    private static async Task runAsync(int seed, int rounds)
    {
        var random = new Random(seed);
        var version = 1;

        // Node id -> the version it runs, so a leader failover can hand the new leader its own version's family
        var versions = new Dictionary<Guid, int>();

        var cluster = new SimulatedCluster(nodeCount: 3, familyFor(version), seed: seed);
        foreach (var id in cluster.NodeIds) versions[id] = version;

        var costs = new Dictionary<Uri, int>();
        cluster.StartCost = uri =>
        {
            if (!costs.TryGetValue(uri, out var cost)) costs[uri] = cost = random.Next(1, 4);
            return cost;
        };

        var quietSince = 0;
        var leaderChangedAt = int.MinValue;
        var log = new List<string>();

        void transition(int round, string what, bool leaderChanged = false)
        {
            quietSince = round;
            if (leaderChanged) leaderChangedAt = round;
            log.Add($"round {round}: {what}");
        }

        string where(int round) => $"seed {seed}, round {round}; last transitions: {string.Join("; ", log.TakeLast(5))}";

        for (var round = 1; round <= rounds; round++)
        {
            var roll = random.NextDouble();

            if (roll < 0.05 && cluster.NodeIds.Count < MaxNodes)
            {
                // A node joins: a replica of the current version, or the first node of the next one
                var bump = random.NextDouble() < 0.3;
                if (bump) version++;
                var id = cluster.AddNode(uris(declared(version)));
                versions[id] = version;
                transition(round, bump ? $"node joined with NEW version v{version}" : $"node joined with v{version}");
            }
            else if (roll < 0.09 && cluster.NodeIds.Count > MinNodes)
            {
                // A non-leader node leaves with everything it was running
                var candidates = cluster.NodeIds.Where(x => x != cluster.LeaderNodeId).ToArray();
                var leaving = candidates[random.Next(candidates.Length)];
                var leavingVersion = versions[leaving];
                cluster.RemoveNode(leaving);
                versions.Remove(leaving);
                transition(round, $"node v{leavingVersion} left");
            }
            else if (roll < 0.11 && cluster.NodeIds.Count > 1)
            {
                // The leader is replaced; the elected node's own store enumerates its own version
                var candidates = cluster.NodeIds.Where(x => x != cluster.LeaderNodeId).ToArray();
                var elected = candidates[random.Next(candidates.Length)];
                var electedVersion = versions[elected];
                var newId = cluster.FailOverLeaderTo(elected, familyFor(electedVersion));
                versions.Remove(elected);
                versions[newId] = electedVersion;
                transition(round, $"leader failed over to a v{electedVersion} node", leaderChanged: true);
            }
            else if (roll < 0.13 && cluster.NodeIds.Count > MinNodes)
            {
                // The leader dies: a peer is elected and the old leader's node is gone
                var dying = cluster.LeaderNodeId;
                var candidates = cluster.NodeIds.Where(x => x != dying).ToArray();
                var elected = candidates[random.Next(candidates.Length)];
                var electedVersion = versions[elected];
                var newId = cluster.FailOverLeaderTo(elected, familyFor(electedVersion));
                versions.Remove(elected);
                versions[newId] = electedVersion;
                cluster.RemoveNode(dying);
                versions.Remove(dying);
                transition(round, $"leader died, a v{electedVersion} node took over", leaderChanged: true);
            }

            var doubleStartsBefore = cluster.DoubleStartReports.Count;
            var commands = await cluster.RunRoundAsync();

            // Invariants that hold on every round, transition or not.
            cluster.UndeclaredPlacements.ShouldBeEmpty(where(round));

            // A double start is only ever legitimate right after a leader change: the new leader has an empty
            // pending ledger and cannot see starts its predecessor dispatched, so it may re-decide one to a
            // different node. Anywhere else it is a defect. Either way the duplicate must be healed by the
            // time the fleet settles (asserted below), which is GH-2602's job.
            if (cluster.DoubleStartReports.Count > doubleStartsBefore && round - leaderChangedAt > 2)
            {
                throw new ShouldAssertException(
                    $"double start outside a leader change: {cluster.DoubleStartReports[^1]}; {where(round)}");
            }

            var settled = commands.Count == 0 && cluster.InFlightStarts == 0
                          && cluster.RunningAgents.Count == cluster.AllAgents.Length;

            if (round - quietSince >= QuietRoundsToConverge)
            {
                settled.ShouldBeTrue(
                    $"not converged {round - quietSince} rounds after the last transition: {commands.Count} commands, " +
                    $"{cluster.InFlightStarts} in flight, {cluster.RunningAgents.Count}/{cluster.AllAgents.Length} running; {where(round)}");

                cluster.DuplicateCopies.ShouldBe(0, $"duplicate copies not healed; {where(round)}");

                assertDatabaseAffinity(cluster, where(round));

                cluster.EvaluationTimes[^1].ShouldBeLessThan(2.Seconds(), $"settled evaluation cost; {where(round)}");
            }
        }
    }

    /// <summary>
    ///     The bound group affinity exists for. A database's agents are sub-partitioned by the set of nodes that
    ///     declare them (<c>AssignmentGrid.capabilityKey</c>): mid-rollout the bumped projection's agents are
    ///     declared by one version's nodes, the unchanged projections' agents by every node, so a database spans
    ///     one host per distinct declaring set — never one per agent.
    /// </summary>
    private static void assertDatabaseAffinity(SimulatedCluster cluster, string context)
    {
        var declaredBy = cluster.Declarations.ToDictionary(x => x.NodeId, x => x.Capabilities.ToHashSet());

        string declaringSet(Uri uri) => string.Join(",",
            declaredBy.Where(x => x.Value.Contains(uri)).Select(x => x.Key).OrderBy(x => x));

        foreach (var db in Databases)
        {
            var agents = cluster.RunningAssignments.Where(x => x.Key.Host == db).ToList();
            var hosts = agents.Select(x => x.Value).Distinct().Count();
            var partitions = agents.Select(x => declaringSet(x.Key)).Distinct().Count();

            hosts.ShouldBeLessThanOrEqualTo(partitions,
                $"{db} is hosted by {hosts} nodes but its agents form {partitions} capability partition(s); {context}");
        }
    }
}
