using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Weasel.Postgresql;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Xunit;

namespace PostgresqlTests;

// GH-4672, reported by @ogysha. Balanced mode enforces a paused agent restriction through
// AssignmentGrid.ApplyRestrictions, but Solo mode has no assignment grid: its health-check loop calls
// startAllAgentsAsync every CheckAssignmentPeriod, which started every URI each family knew about and
// never read the restrictions. So an operator could pause an agent and stop it, and the loop put it
// straight back within one period -- a pause could not be made to stick at all in a single-instance
// deployment.
public class solo_mode_honors_paused_agents_4672 : IAsyncLifetime
{
    private IHost _host = null!;

    private const string SchemaName = "solo_paused_4672";

    public async ValueTask InitializeAsync()
    {
        // Agent restrictions are DURABLE, which is the whole point of the issue -- so a pause left behind
        // by an earlier run of this class would still be in force here, and the arrange step below
        // ("all twelve agents are running") would never be satisfied. Start from no schema at all.
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync(SchemaName);
            await conn.CloseAsync();
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IAgentFamily, FakeAgentFamily>();

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);

                opts.Durability.Mode = DurabilityMode.Solo;

                // Short enough that the regression -- a restart on the next tick -- happens well inside
                // the waits below rather than being a race the test could win by accident.
                opts.Durability.CheckAssignmentPeriod = 100.Milliseconds();
            })
            .StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task a_paused_agent_is_not_restarted_by_the_solo_assignment_loop()
    {
        var runtime = _host.GetRuntime();

        await waitUntilRunningAsync(runtime, FakeAgentFamily.Names.Length);

        var paused = new Uri("fake://three");
        runtime.Agents.AllRunningAgentUris().ShouldContain(paused);

        var restrictions = new AgentRestrictions([]);
        restrictions.PauseAgent(paused);
        await runtime.Agents.ApplyRestrictionsAsync(restrictions, CancellationToken.None);

        // The operator's own second step, and the one the issue reports as being undone: Solo mode does
        // not carry the pause out to a running agent on its own.
        await runtime.Agents.StopLocallyAsync(paused);
        runtime.Agents.AllRunningAgentUris().ShouldNotContain(paused);

        // Several assignment periods, so a restart would certainly have happened by now
        await Task.Delay(1.Seconds(), TestContext.Current.CancellationToken);

        runtime.Agents.AllRunningAgentUris().ShouldNotContain(paused);

        // ...and the pause is scoped to the one agent: its siblings keep being started as before, which
        // is what makes this different from "the loop stopped working".
        runtime.Agents.AllRunningAgentUris().ShouldContain(new Uri("fake://four"));
    }

    [Fact]
    public async Task clearing_the_restriction_lets_the_solo_loop_start_the_agent_again()
    {
        var runtime = _host.GetRuntime();

        await waitUntilRunningAsync(runtime, FakeAgentFamily.Names.Length);

        var uri = new Uri("fake://seven");

        var pause = new AgentRestrictions([]);
        pause.PauseAgent(uri);
        await runtime.Agents.ApplyRestrictionsAsync(pause, CancellationToken.None);
        await runtime.Agents.StopLocallyAsync(uri);

        await Task.Delay(500.Milliseconds(), TestContext.Current.CancellationToken);
        runtime.Agents.AllRunningAgentUris().ShouldNotContain(uri);

        // Unpausing has to be enough on its own -- the Solo loop is what brings the agent back, so if it
        // read the restrictions once and cached them, this is where that would show.
        //
        // Loaded first on purpose: RestartAgent only flips an EXISTING paused entry, so applying a fresh
        // empty AgentRestrictions would merge no changes and quietly do nothing.
        var state = await runtime.Storage.Nodes
            .LoadNodeAgentStateAsync(TestContext.Current.CancellationToken);
        var current = state.Restrictions;
        current.RestartAgent(uri);
        await runtime.Agents.ApplyRestrictionsAsync(current, CancellationToken.None);

        await waitUntilAsync(() => runtime.Agents.AllRunningAgentUris().Contains(uri), 5.Seconds());
    }

    private static Task waitUntilRunningAsync(IWolverineRuntime runtime, int count)
        => waitUntilAsync(() => runtime.Agents.AllRunningAgentUris().Count(x => x.Scheme == "fake") >= count,
            10.Seconds());

    private static async Task waitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50.Milliseconds());
        }

        throw new TimeoutException($"Condition was not met within {timeout}");
    }
}
