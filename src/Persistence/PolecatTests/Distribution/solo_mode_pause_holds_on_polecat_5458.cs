using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Events.Projections;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using PolecatTests.Distribution.TripDomain;
using Shouldly;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;

namespace PolecatTests.Distribution;

// GH-5458: the Polecat counterpart of GH-4672 (PostgresqlTests/solo_mode_honors_paused_agents_4672). On a
// DurabilityMode.Solo host with UseWolverineManagedEventSubscriptionDistribution, an operator pauses one
// projection shard through IAgentRuntime.ApplyRestrictionsAsync -- and nothing else, which is exactly what the
// reporter's caller does. The restriction must hold: the shard stops, stays stopped across many Solo
// assignment periods, and does not advance while new events arrive, while its siblings carry on.
public class solo_mode_pause_holds_on_polecat_5458 : IAsyncLifetime
{
    private const string SchemaName = "polecat_solo_pause_5458";

    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        // Agent restrictions are durable, so a pause left behind by an earlier run would still be in force.
        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync(SchemaName);
            await conn.CloseAsync();
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Short enough that a restart by the Solo loop would certainly happen inside the waits below
                opts.Durability.CheckAssignmentPeriod = 100.Milliseconds();

                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = SchemaName;

                        m.Projections.Add<TripProjection>(ProjectionLifecycle.Async);
                        m.Projections.Add<DayProjection>(ProjectionLifecycle.Async);
                        m.Projections.Add<DistanceProjection>(ProjectionLifecycle.Async);
                    })
                    .IntegrateWithWolverine(o =>
                    {
                        o.UseWolverineManagedEventSubscriptionDistribution = true;
                        o.MessageStorageSchemaName = SchemaName;
                        o.TransportSchemaName = SchemaName;
                    })
                    .ApplyAllDatabaseChangesOnStartup();

                opts.Discovery.DisableConventionalDiscovery();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task a_paused_shard_stays_paused_and_does_not_advance_in_solo_mode()
    {
        var runtime = _host.GetRuntime();
        var ct = TestContext.Current.CancellationToken;

        await waitUntilAsync(() => runningShards(runtime).Length == 3, 30.Seconds());

        var day = runningShards(runtime).Single(x => x.AbsolutePath.TrimEnd('/').EndsWith("/day/all"));

        // Everything the projections see so far is caught up before the pause
        await appendTripsAsync(5);
        await waitUntilAsync(() => progressOf("Day:All") >= highestSequence(), 30.Seconds());

        // The reporter's caller, verbatim: restrictions only, no StopLocallyAsync
        var restrictions = new AgentRestrictions();
        restrictions.PauseAgent(day);
        await runtime.Agents.ApplyRestrictionsAsync(restrictions, ct);

        var state = await runtime.Storage.Nodes.LoadNodeAgentStateAsync(ct);
        state.Restrictions.FindPausedAgentUris().ShouldContain(day);

        // The pause has to stop the shard on this node, not only persist the row
        runtime.Agents.AllRunningAgentUris().ShouldNotContain(day);

        var pausedAt = progressOf("Day:All");

        // New events, then many Solo assignment periods
        await appendTripsAsync(5);
        var high = highestSequence();
        await waitUntilAsync(() => progressOf("Trip:All") >= high, 30.Seconds());
        await Task.Delay(2.Seconds(), ct);

        runtime.Agents.AllRunningAgentUris().ShouldNotContain(day);
        progressOf("Day:All").ShouldBe(pausedAt);

        // Scoped to the one shard: its siblings keep running
        runningShards(runtime).Length.ShouldBe(2);
    }

    [Fact]
    public async Task lifting_the_pause_lets_the_solo_loop_restart_the_shard()
    {
        var runtime = _host.GetRuntime();
        var ct = TestContext.Current.CancellationToken;

        await waitUntilAsync(() => runningShards(runtime).Length == 3, 30.Seconds());
        var day = runningShards(runtime).Single(x => x.AbsolutePath.TrimEnd('/').EndsWith("/day/all"));

        var pause = new AgentRestrictions();
        pause.PauseAgent(day);
        await runtime.Agents.ApplyRestrictionsAsync(pause, ct);
        runtime.Agents.AllRunningAgentUris().ShouldNotContain(day);

        var state = await runtime.Storage.Nodes.LoadNodeAgentStateAsync(ct);
        var current = state.Restrictions;
        current.RestartAgent(day);
        await runtime.Agents.ApplyRestrictionsAsync(current, ct);

        await waitUntilAsync(() => runtime.Agents.AllRunningAgentUris().Contains(day), 10.Seconds());

        await appendTripsAsync(3);
        await waitUntilAsync(() => progressOf("Day:All") >= highestSequence(), 30.Seconds());
    }

    private static Uri[] runningShards(IWolverineRuntime runtime)
        => runtime.Agents.AllRunningAgentUris()
            .Where(x => x.Scheme == EventSubscriptionAgentFamily.SchemeName)
            .ToArray();

    private async Task appendTripsAsync(int count)
    {
        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();
        for (var i = 0; i < count; i++)
        {
            session.Events.StartStream<Trip>(Guid.NewGuid(), new TripStarted { Day = i + 1 },
                new TripEnded { Day = i + 2 });
        }

        await session.SaveChangesAsync();
    }

    private static long progressOf(string name)
    {
        using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select last_seq_id from {SchemaName}.pc_event_progression where name = @name";
        cmd.Parameters.AddWithValue("@name", name);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    private static long highestSequence()
    {
        using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select coalesce(max(seq_id), 0) from {SchemaName}.pc_events";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static async Task waitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100.Milliseconds());
        }

        throw new TimeoutException($"Condition was not met within {timeout}");
    }
}
