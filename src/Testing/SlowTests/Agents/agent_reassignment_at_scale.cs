using System.Diagnostics;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Xunit;

namespace SlowTests.Agents;

/// <summary>
/// Reproduction harness for GH-3753 / GH-3749: "node assignment is very slow, or never completes, when a
/// release contains a projection version bump".
///
/// <para>The production shape (512 tenant databases, ~6,500 agents, five nodes): a deploy with no version
/// bump converges in minutes; a deploy WITH one leaves one node holding everything, its peers frozen at
/// zero, and the total placed count marching BACKWARDS. The mechanism (GH-3749) is that a reassignment
/// used to run in the DESTINATION node's dispatcher lane while doing a blocking stop round trip against
/// the SOURCE — and a version bump is exactly what makes the source slow to answer a stop, so a couple of
/// dozen reassignments at the head of a healthy destination's lane starved the batched fresh starts queued
/// behind them. Reassignments were also the one command never chunked by AgentStartBatchSize.</para>
///
/// <para>This test builds the deploy in miniature: one node running the whole old generation with SLOW
/// STOPS enabled (the source is busy replaying), then two fresh nodes join at the same moment a new
/// generation of agents arrives — so reassignments of the old agents and first-time placements of the new
/// ones are in flight together, which is the combination that separated the broken deploys from the
/// healthy control.</para>
/// </summary>
[Collection("agent_scale")]
public class agent_reassignment_at_scale : IAsyncLifetime
{
    private const string SchemaName = "agent_rescale";

    private const int NodeCount = 3;
    private const int OldGeneration = 240; // running on node 1 before the "deploy"
    private const int FullUniverse = 480;  // old + the new generation arriving with the deploy

    private static readonly TimeSpan StartDelay = 250.Milliseconds();
    private static readonly TimeSpan StopDelay = 2.Seconds();

    private readonly ITestOutputHelper _output;
    private readonly List<IHost> _hosts = new();
    private readonly AgentStartTelemetry _telemetry = new(OldGeneration, StartDelay);

    public agent_reassignment_at_scale(ITestOutputHelper output)
    {
        _output = output;
    }

    public async ValueTask InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync(SchemaName);
        await conn.CloseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        // The slow stop stands in for a busy source during the test; teardown should not pay it for
        // hundreds of agents.
        _telemetry.StopDelay = TimeSpan.Zero;

        foreach (var host in _hosts)
        {
            host.GetRuntime().Agents.DisableHealthChecks();
        }

        _hosts.Reverse();
        foreach (var host in _hosts)
        {
            try
            {
                await host.StopAsync();
                host.Dispose();
            }
            catch (Exception e)
            {
                _output.WriteLine("Error shutting a host down: " + e);
            }
        }
    }

    private Task<IHost> startHostAsync()
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Balanced;

                opts.Durability.FirstHealthCheckExecution = 5.Seconds();
                opts.Durability.HealthCheckPollingTime = 2.Seconds();
                opts.Durability.CheckAssignmentPeriod = 5.Seconds();

                // The shipped defaults, deliberately — the reported cluster's knobs.
                opts.Durability.AgentStartBatchSize = 50;
                opts.Durability.MaxAgentStartParallelism = 10;

                opts.Services.AddSingleton<IAgentFamily>(new SlowStartAgentFamily(_telemetry, opts));

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    [Fact]
    public async Task a_version_bump_deploy_converges_and_never_marches_backwards()
    {
        // ── Phase 1: the world before the deploy — one node owns the whole old generation. ──
        _hosts.Add(await startHostAsync());

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var placed = await takeSampleAsync(stopwatch.Elapsed);
            if (placed.TotalAssigned >= OldGeneration) break;

            stopwatch.Elapsed.ShouldBeLessThan(2.Minutes(),
                $"node 1 never took the old generation; got to {placed.TotalAssigned} of {OldGeneration}");
            await Task.Delay(1.Seconds(), TestContext.Current.CancellationToken);
        }

        _output.WriteLine($"Phase 1: node 1 owns all {OldGeneration} old-generation agents at {stopwatch.Elapsed.TotalSeconds:0}s");

        // ── The deploy: stops become slow (every shard is behind the side-effect gate), a new
        // generation of agents arrives, and two fresh nodes join. ──
        _telemetry.StopDelay = StopDelay;
        _telemetry.GrowUniverseTo(FullUniverse);

        var deployStarted = stopwatch.Elapsed;
        _hosts.AddRange(await Task.WhenAll(startHostAsync(), startHostAsync()));

        var samples = new List<Sample>();
        var hardTimeout = deployStarted + 6.Minutes();

        var runningMax = 0;
        var worstBackslide = 0;
        TimeSpan? freshNodesCarrying = null;
        TimeSpan? convergedAt = null;

        while (stopwatch.Elapsed < hardTimeout)
        {
            await Task.Delay(1.Seconds(), TestContext.Current.CancellationToken);

            var sample = await takeSampleAsync(stopwatch.Elapsed - deployStarted);
            samples.Add(sample);

            // The field failure marched backwards: 2,401 → 1,885 over four minutes, because stops kept
            // landing while the starts that should follow them never ran. A final-count assertion alone
            // would pass right through that, so track the worst dip below the running maximum.
            runningMax = Math.Max(runningMax, sample.TotalAssigned);
            worstBackslide = Math.Max(worstBackslide, runningMax - sample.TotalAssigned);

            // The sharpest #3749 symptom: nodes handed hundreds of fresh placements landed ZERO because
            // reassignment stops against the busy source sat at the head of their lanes.
            if (freshNodesCarrying == null && sample.AssignedPerNode.Count(x => x.Key != 1 && x.Value >= 20) >= 2)
            {
                freshNodesCarrying = sample.Elapsed;
            }

            if (sample.TotalAssigned >= FullUniverse)
            {
                convergedAt = sample.Elapsed;
                break;
            }
        }

        writeReport(samples, convergedAt, freshNodesCarrying, worstBackslide);

        // The healthy nodes must start receiving and starting their FRESH agents while the busy source is
        // still slowly answering reassignment stops — not after every reassignment has drained. Pre-fix,
        // ~40 unbatched reassignments at ~2s+ a stop sat ahead of each fresh node's first chunk, so this
        // took well over 100 seconds; batched into the source's own lane it takes a handful.
        freshNodesCarrying.ShouldNotBeNull("the two fresh nodes never carried 20 agents each");
        freshNodesCarrying.Value.ShouldBeLessThan(60.Seconds());

        // erdtsieck's acceptance criterion for GH-3753, scaled down: after a deploy, every agent assigned
        // across every node, within minutes. The stop work itself is ~80 moves x 2s serial on the source,
        // so the floor is ~160s of honest work; pre-fix the same shape does not converge inside the hard
        // timeout.
        convergedAt.ShouldNotBeNull($"never converged to {FullUniverse}; hard timeout after 6 minutes");
        convergedAt.Value.ShouldBeLessThan(4.Minutes());

        // Never worse than one reassignment chunk in flight: a stop may land before its paired start, but
        // only within the batch being moved. The field failure lost hundreds net.
        worstBackslide.ShouldBeLessThanOrEqualTo(70);

        // And the end state is an actual spread, not one node holding everything.
        var final = samples.Last();
        final.AssignedPerNode.Count.ShouldBe(NodeCount);
        foreach (var pair in final.AssignedPerNode)
        {
            pair.Value.ShouldBeGreaterThan(100);
        }
    }

    private record Sample(TimeSpan Elapsed, Dictionary<int, int> AssignedPerNode)
    {
        public int TotalAssigned => AssignedPerNode.Values.Sum();
    }

    private async Task<Sample> takeSampleAsync(TimeSpan elapsed)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        var perNode = new Dictionary<int, int>();

        await using (var cmd = conn.CreateCommand($@"
select n.node_number, count(a.id)
from {SchemaName}.wolverine_nodes n
left join {SchemaName}.wolverine_node_assignments a
    on a.node_id = n.id and a.id like '{SlowStartAgentFamily.SchemeName}://%'
group by n.node_number
order by n.node_number"))
        {
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                perNode[reader.GetInt32(0)] = (int)reader.GetInt64(1);
            }
        }

        await conn.CloseAsync();

        return new Sample(elapsed, perNode);
    }

    private void writeReport(List<Sample> samples, TimeSpan? convergedAt, TimeSpan? freshNodesCarrying,
        int worstBackslide)
    {
        _output.WriteLine(
            $"{OldGeneration} old agents on node 1, universe grown to {FullUniverse} at deploy, {NodeCount} nodes, {StartDelay.TotalMilliseconds}ms per start, {StopDelay.TotalSeconds}s per stop");
        _output.WriteLine("");
        _output.WriteLine("elapsed | assigned per node             | total");
        _output.WriteLine("--------+-------------------------------+------");

        foreach (var sample in samples)
        {
            var per = sample.AssignedPerNode.OrderBy(x => x.Key)
                .Select(x => $"{x.Key}:{x.Value,4}").Join("  ");
            _output.WriteLine($"{sample.Elapsed.TotalSeconds,6:0}s | {per,-29} | {sample.TotalAssigned,5}");
        }

        _output.WriteLine("");
        _output.WriteLine($"Fresh nodes carrying ≥20 each: {(freshNodesCarrying.HasValue ? $"{freshNodesCarrying.Value.TotalSeconds:0.0}s" : "NEVER")}");
        _output.WriteLine($"Converged at:                  {(convergedAt.HasValue ? $"{convergedAt.Value.TotalSeconds:0.0}s" : "NEVER")}");
        _output.WriteLine($"Worst backslide:               {worstBackslide} agents below the running max");
        _output.WriteLine($"Peak concurrent agent starts:  {_telemetry.PeakInFlight}");
        _output.WriteLine($"Agent starts per node:         {_telemetry.StartsPerNode.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}").Join("  ")}");
    }
}
