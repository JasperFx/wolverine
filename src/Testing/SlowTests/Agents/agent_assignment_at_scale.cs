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
/// Reproduction harness for GH-3698: "Agent assignment stalls at a few hundred of ~5650 agents and the
/// leader goes idle".
///
/// <para>The reported production shape is a cluster of five healthy nodes advertising ~5,240 agents
/// (Marten event subscriptions across 512 tenant databases plus one durability agent per database) where
/// the leader assigns a few dozen agents to each follower, then stops issuing chunks to them entirely.
/// Over 40 minutes of stable topology the followers do not gain a single agent; the only assignment count
/// that grows is the leader's own, at roughly 10 agents/minute, and it grows without a single
/// <c>AssignmentChanged</c> node record being written. Nothing is logged and no node is unhealthy.</para>
///
/// <para>This test builds the same shape in miniature: a large agent universe whose members take real
/// wall-clock time to start (a Marten subscription agent start is a daemon shard spin-up, and a projection
/// version bump turns every start into a replay), spread over a three-node cluster on a real Postgres
/// store. The interesting numbers are collected on a polling loop and dumped to test output so the failure
/// can be read directly rather than inferred:</para>
///
/// <list type="bullet">
/// <item>per-node assignment counts over time — does every node keep gaining, or only one?</item>
/// <item>cluster-wide peak concurrent agent starts — is more than one node ever starting at a time?</item>
/// <item>the longest gap between two leader assignment evaluations — is the leader's health-check loop
/// making progress, or is it parked inside the agent command drain?</item>
/// </list>
/// </summary>
[Collection("agent_scale")]
public class agent_assignment_at_scale : IAsyncLifetime
{
    private const string SchemaName = "agent_scale";

    // Sized so a correctly-parallelised cluster converges in well under the budget below while the
    // observed serial behaviour cannot. See the assertions for the arithmetic.
    private const int NodeCount = 3;
    private const int AgentCount = 900;
    private static readonly TimeSpan StartDelay = 1.Seconds();

    private readonly ITestOutputHelper _output;
    private readonly List<IHost> _hosts = new();
    private readonly AgentStartTelemetry _telemetry = new(AgentCount, StartDelay);

    public agent_assignment_at_scale(ITestOutputHelper output)
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

                // Give every node in the cluster time to register itself before the first assignment
                // evaluation runs, so the leader distributes against the full topology exactly once
                // instead of dumping the whole universe onto whichever node happened to boot first.
                // This models the reported scenario: five nodes already healthy, stable topology.
                opts.Durability.FirstHealthCheckExecution = 20.Seconds();
                opts.Durability.HealthCheckPollingTime = 2.Seconds();
                opts.Durability.CheckAssignmentPeriod = 5.Seconds();

                // Deliberately the shipped defaults -- these are the knobs the reported cluster was
                // running on (AgentStartBatchSize 50, MaxAgentStartParallelism 10).
                opts.Durability.AgentStartBatchSize = 50;
                opts.Durability.MaxAgentStartParallelism = 10;

                opts.Services.AddSingleton<IAgentFamily>(new SlowStartAgentFamily(_telemetry, opts));

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    [Fact]
    public async Task the_whole_agent_universe_is_distributed_across_every_node()
    {
        // Start every node inside the FirstHealthCheckExecution window so the first evaluation sees the
        // full cluster.
        var hosts = await Task.WhenAll(Enumerable.Range(0, NodeCount).Select(_ => startHostAsync()));
        _hosts.AddRange(hosts);

        var samples = new List<Sample>();
        var stopwatch = Stopwatch.StartNew();
        var hardTimeout = 6.Minutes();

        DateTimeOffset? lastEvaluation = null;
        DateTimeOffset lastEvaluationChangeAt = DateTimeOffset.UtcNow;
        var longestEvaluationGap = TimeSpan.Zero;
        TimeSpan? convergedAt = null;

        while (stopwatch.Elapsed < hardTimeout)
        {
            await Task.Delay(1.Seconds());

            var sample = await takeSampleAsync(stopwatch.Elapsed);
            samples.Add(sample);

            // How long has the leader gone without completing an assignment evaluation? The reported
            // cluster went 40+ minutes with zero AssignmentChanged records while remaining healthy.
            var evaluation = currentLeaderEvaluationTime();
            if (evaluation != lastEvaluation)
            {
                lastEvaluation = evaluation;
                lastEvaluationChangeAt = DateTimeOffset.UtcNow;
            }
            else if (lastEvaluation.HasValue)
            {
                var gap = DateTimeOffset.UtcNow - lastEvaluationChangeAt;
                if (gap > longestEvaluationGap) longestEvaluationGap = gap;
            }

            if (sample.TotalAssigned >= AgentCount)
            {
                convergedAt = stopwatch.Elapsed;
                break;
            }
        }

        writeReport(samples, convergedAt, longestEvaluationGap);

        // A correctly fanned-out cluster starts NodeCount chunks concurrently, so the cluster-wide
        // in-flight count exceeds a single node's MaxAgentStartParallelism. If the leader drains its
        // command list one command at a time, only one node is ever starting agents and this never
        // climbs above 10.
        _telemetry.PeakInFlight.ShouldBeGreaterThan(10);

        // 900 agents x 1s, spread over 3 nodes x MaxAgentStartParallelism 10, is ~30s of start work.
        // Funnelled through one node at a time it is ~90s. The budget below is generous for the former
        // and unreachable for the latter.
        convergedAt.ShouldNotBeNull();
        convergedAt.Value.ShouldBeLessThan(75.Seconds());

        // Every node should be carrying a share of the universe.
        var final = samples.Last();
        foreach (var pair in final.AssignedPerNode)
        {
            pair.Value.ShouldBeGreaterThan(0);
        }
    }

    private DateTimeOffset? currentLeaderEvaluationTime()
    {
        foreach (var host in _hosts)
        {
            var runtime = host.GetRuntime();
            var controller = runtime.NodeController;
            if (controller is { IsLeader: true })
            {
                return controller.LastAssignments?.EvaluationTime;
            }
        }

        return null;
    }

    private record Sample(TimeSpan Elapsed, Dictionary<int, int> AssignedPerNode, int AssignmentChangedRecords)
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

        var records = 0;
        await using (var cmd = conn.CreateCommand(
                         $"select count(*) from {SchemaName}.wolverine_node_records where event_name = 'AssignmentChanged'"))
        {
            records = (int)(long)(await cmd.ExecuteScalarAsync())!;
        }

        await conn.CloseAsync();

        return new Sample(elapsed, perNode, records);
    }

    private void writeReport(List<Sample> samples, TimeSpan? convergedAt, TimeSpan longestEvaluationGap)
    {
        _output.WriteLine(
            $"{AgentCount} agents, {NodeCount} nodes, {StartDelay.TotalMilliseconds}ms per agent start, batch size 50, start parallelism 10");
        _output.WriteLine("");
        _output.WriteLine("elapsed | assigned per node                     | total | AssignmentChanged");
        _output.WriteLine("--------+--------------------------------------+-------+------------------");

        foreach (var sample in samples)
        {
            var per = sample.AssignedPerNode.OrderBy(x => x.Key)
                .Select(x => $"{x.Key}:{x.Value,4}").Join("  ");
            _output.WriteLine(
                $"{sample.Elapsed.TotalSeconds,6:0}s | {per,-36} | {sample.TotalAssigned,5} | {sample.AssignmentChangedRecords,5}");
        }

        _output.WriteLine("");
        _output.WriteLine($"Converged at:                  {(convergedAt.HasValue ? $"{convergedAt.Value.TotalSeconds:0.0}s" : "NEVER")}");
        _output.WriteLine($"Peak concurrent agent starts:  {_telemetry.PeakInFlight} (cluster-wide; {NodeCount} nodes x 10 parallelism = {NodeCount * 10} available)");
        _output.WriteLine($"Agent starts per node:         {_telemetry.StartsPerNode.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}").Join("  ")}");
        _output.WriteLine($"Longest leader evaluation gap: {longestEvaluationGap.TotalSeconds:0.0}s (CheckAssignmentPeriod is 5s)");
    }
}
