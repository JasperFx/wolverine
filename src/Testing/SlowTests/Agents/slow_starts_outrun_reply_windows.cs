using System.Collections.Concurrent;
using System.Diagnostics;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
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
/// Reproduction harness for GH-3748 / GH-3750: agent starts that take longer than the batch's
/// request/reply window.
///
/// <para>The reported production shape (same cluster as GH-3753): a projection version bump turns every
/// subscription shard start into a bounded replay measured at 27s p50 / 215s max — so honest, healthy
/// work outruns every achievable reply window. The old protocol treated the window elapsing as failure:
/// the leader released the whole chunk, the pending-assignment ledger's TTL expired, and the next
/// evaluation re-placed agents that were still mid-start onto other nodes. Field measurement: 369 of 902
/// distinct agents started more than once in twelve minutes, every repeat on a different node, while
/// thousands of never-placed agents waited.</para>
///
/// <para>This test builds that shape in miniature: chunks whose start work (12 agents x 10s at
/// parallelism 2 = 60s) exceeds their reply window (30s + 12s = 42s). With the GH-3748 fix the leader
/// races the reply against <c>QueryAgentPresence</c> progress polling and the destination executes the
/// batch off its serial control lane, so the cluster must converge with every agent started exactly
/// once. On the old protocol this exact shape churns: chunks time out at 42s, agents re-place onto other
/// nodes mid-start, and the same agent completes StartAsync on more than one node.</para>
/// </summary>
[Collection("agent_scale")]
public class slow_starts_outrun_reply_windows : IAsyncLifetime
{
    private const string SchemaName = "agent_patience";

    private const int NodeCount = 3;
    private const int AgentCount = 36;

    // 12 agents per chunk at parallelism 2 and 10s per start = 60s of work against a 42s reply window
    private const int BatchSize = 12;
    private const int StartParallelism = 2;
    private static readonly TimeSpan StartDelay = 10.Seconds();

    private readonly ITestOutputHelper _output;
    private readonly List<IHost> _hosts = new();
    private readonly AgentStartTelemetry _telemetry = new(AgentCount, StartDelay);

    public slow_starts_outrun_reply_windows(ITestOutputHelper output)
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

    // Cluster-wide capture of the agent plane's log stream, dumped on failure. The interesting decisions
    // (retry-due re-emissions, reverts, reassignments, confirmations) are only visible here.
    private readonly ConcurrentQueue<string> _log = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private sealed class CollectingLoggerProvider(ConcurrentQueue<string> sink, Stopwatch clock, int hostIndex)
        : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CollectingLogger(sink, clock, hostIndex, categoryName);

        public void Dispose()
        {
        }

        private sealed class CollectingLogger(ConcurrentQueue<string> sink, Stopwatch clock, int hostIndex, string category)
            : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug &&
                (category.Contains("Agent") || category.Contains("Wolverine.Runtime.WolverineRuntime") || category.Contains("NodeAgentController"));

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                sink.Enqueue($"{clock.Elapsed.TotalSeconds,7:0.0}s h{hostIndex} [{logLevel.ToString()[0]}] {formatter(state, exception)}{(exception == null ? "" : " EX: " + exception.Message)}");
            }
        }
    }

    private int _hostIndex;

    private Task<IHost> startHostAsync()
    {
        var hostIndex = Interlocked.Increment(ref _hostIndex);
        return Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddProvider(new CollectingLoggerProvider(_log, _clock, hostIndex)))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Balanced;

                // Let the whole cluster register before the first evaluation so the leader distributes
                // against the full topology once — the reported scenario is a stable, healthy cluster.
                opts.Durability.FirstHealthCheckExecution = 20.Seconds();
                opts.Durability.HealthCheckPollingTime = 2.Seconds();
                opts.Durability.CheckAssignmentPeriod = 5.Seconds();

                opts.Durability.AgentStartBatchSize = BatchSize;
                opts.Durability.MaxAgentStartParallelism = StartParallelism;

                // Tight enough that the polls, not the reply window, are what carries the wait
                opts.Durability.AgentProgressPollInterval = 5.Seconds();

                opts.Services.AddSingleton<IAgentFamily>(new SlowStartAgentFamily(_telemetry, opts));

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    [Fact]
    public async Task chunks_slower_than_the_reply_window_converge_without_duplicate_starts()
    {
        // Heterogeneous start costs, the field shape: a third of the universe starts in 6s, a third in
        // 12s, a third in 18s, so the nodes finish their chunks at very different times (36s / 72s /
        // 108s of work against a 42s reply window) and the leader has to keep waiting on nodes that are
        // slow but demonstrably progressing.
        _telemetry.DelayOverride = uri =>
        {
            var index = int.Parse(uri.AbsolutePath.TrimStart('/').Split('/').Last());
            return (6 * (1 + index / 12)).Seconds();
        };

        var hosts = await Task.WhenAll(Enumerable.Range(0, NodeCount).Select(_ => startHostAsync()));
        _hosts.AddRange(hosts);

        var samples = new List<Sample>();
        var stopwatch = Stopwatch.StartNew();
        var hardTimeout = 5.Minutes();
        TimeSpan? convergedAt = null;

        while (stopwatch.Elapsed < hardTimeout)
        {
            await Task.Delay(2.Seconds(), TestContext.Current.CancellationToken);

            var sample = await takeSampleAsync(stopwatch.Elapsed);
            samples.Add(sample);

            if (sample.TotalAssigned >= AgentCount)
            {
                convergedAt = stopwatch.Elapsed;
                break;
            }
        }

        writeReport(samples, convergedAt);

        foreach (var line in await churnFromNodeRecordsAsync())
        {
            _output.WriteLine(line);
        }

        _output.WriteLine("");
        _output.WriteLine("--- captured agent-plane log ---");
        foreach (var line in _log)
        {
            _output.WriteLine(line);
        }

        // The whole universe is placed even though every chunk's work outran its reply window. On the
        // old protocol the timeout was treated as failure and this converges late or never.
        convergedAt.ShouldNotBeNull();

        // ~20s registration + 60s for one chunk's work per node (all three concurrently) + control
        // round-trips. Generous so only real churn — restarted work — can spend it.
        convergedAt.Value.ShouldBeLessThan(3.Minutes());

        // THE GH-3750 churn metric, straight from the field report: no agent may complete a start more
        // than once anywhere in the cluster. A re-placed mid-start agent completes StartAsync twice, on
        // two different nodes.
        var totalCompletedStarts = _telemetry.StartsPerNode.Values.Sum();
        totalCompletedStarts.ShouldBe(AgentCount);

        // And the distribution actually used the whole cluster
        var final = samples.Last();
        foreach (var pair in final.AssignedPerNode)
        {
            pair.Value.ShouldBeGreaterThan(0);
        }
    }

    private record Sample(TimeSpan Elapsed, Dictionary<int, int> AssignedPerNode, int RegisteredNodes)
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

        return new Sample(elapsed, perNode, perNode.Count);
    }

    // The reporter's own churn query from GH-3750: agents started more than once, and whether the
    // repeats landed on more than one node.
    private async Task<List<string>> churnFromNodeRecordsAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        var lines = new List<string>();
        await using (var cmd = conn.CreateCommand($@"
with s as (
  select description as agent, count(*) as n, count(distinct node_number) as nodes
  from {SchemaName}.wolverine_node_records
  where event_name = 'AgentStarted' and description like '{SlowStartAgentFamily.SchemeName}://%'
  group by 1)
select n as times_started, count(*) as agents, sum((nodes > 1)::int) as on_multiple_nodes
from s group by 1 order by 1 desc"))
        {
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                lines.Add(
                    $"started {reader.GetInt64(0)}x: {reader.GetInt64(1)} agents ({reader.GetInt64(2)} on multiple nodes)");
            }
        }

        await conn.CloseAsync();
        return lines;
    }

    private void writeReport(List<Sample> samples, TimeSpan? convergedAt)
    {
        _output.WriteLine(
            $"{AgentCount} agents, {NodeCount} nodes, 6s/12s/18s per agent start by index, batch size {BatchSize}, start parallelism {StartParallelism}");
        // The reply window for a chunk of 12 is 30s + 12 x 1s (AgentBatchTimeouts.ReplyWindowFor)
        _output.WriteLine($"Reply window per chunk: {30 + BatchSize}s; work per chunk: 36s/72s/108s by index block");
        _output.WriteLine("");
        _output.WriteLine("elapsed | nodes | assigned per node          | total");
        _output.WriteLine("--------+-------+---------------------------+------");

        foreach (var sample in samples)
        {
            var per = sample.AssignedPerNode.OrderBy(x => x.Key)
                .Select(x => $"{x.Key}:{x.Value,3}").Join("  ");
            _output.WriteLine($"{sample.Elapsed.TotalSeconds,6:0}s | {sample.RegisteredNodes,5} | {per,-25} | {sample.TotalAssigned,4}");
        }

        _output.WriteLine("");
        _output.WriteLine($"Converged at:            {(convergedAt.HasValue ? $"{convergedAt.Value.TotalSeconds:0.0}s" : "NEVER")}");
        _output.WriteLine($"Completed starts:        {_telemetry.StartsPerNode.Values.Sum()} (agent universe is {AgentCount}; any excess is a duplicate start)");
        _output.WriteLine($"Starts per node:         {_telemetry.StartsPerNode.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}").Join("  ")}");
    }
}
