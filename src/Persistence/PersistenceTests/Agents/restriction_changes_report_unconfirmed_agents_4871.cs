using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace PersistenceTests.Agents;

/// <summary>
/// GH-4871. <c>ApplyRestrictionsAsync</c> is the operator's entry point for pause / restart / pin, and since
/// GH-3698 it throws when a command carrying the change <i>failed</i>. An <i>unconfirmed</i> start was never a
/// failure in that sense: <c>AssignAgents</c> races the <c>AgentsStarted</c> reply against presence polls, logs
/// "confirmed 0 of 1 requested agents" when nothing confirms, and returns an empty command set. So a restart
/// that started nothing returned exactly like one that worked, and CritterWatch (#1393) acked it as Succeeded
/// while the projection ran on no node (the GH-4868 field case).
///
/// <para>
/// The store knows the truth: a node writes the agent's assignment row before it replies to a start. So after
/// the drain, every agent a release or pin should have put to work is awaited in the persisted node state for a
/// bounded time, and one still assigned nowhere throws <see cref="AgentRestrictionsNotConfirmedException" />
/// naming it. The restriction itself is persisted either way; the leader keeps re-evaluating. Only the start
/// direction is verified: a stop's assignment delete is best-effort, and a row outliving a stop that worked must
/// not turn a pause into a false failure.
/// </para>
/// </summary>
public class restriction_changes_report_unconfirmed_agents_4871 : IAsyncDisposable
{
    private readonly ITestOutputHelper _output;
    private IHost? _host;
    private WolverineRuntime _runtime = null!;

    public restriction_changes_report_unconfirmed_agents_4871(ITestOutputHelper output)
    {
        _output = output;
    }

    public async ValueTask DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    private async Task startHostAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await conn.DropSchemaAsync("bug4871", ct: TestContext.Current.CancellationToken);
            await conn.CloseAsync();
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IAgentFamily, FakeAgentFamily>();
                opts.Services.AddSingleton<IAgentFamily, BrokenStartAgentFamily>();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "bug4871");
                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(_output));
                opts.Services.AddResourceSetupOnStartup();

                opts.Durability.Mode = DurabilityMode.Balanced;

                // As in Bug_3666: silence the background loop so the only evaluations are the ones this test
                // drives, which is what makes the outcome deterministic rather than a race against polling.
                opts.Durability.HealthCheckPollingTime = 1.Hours();
                opts.Durability.CheckAssignmentPeriod = 1.Hours();

                // The broken agent fails fast, so these only bound the wait if a reply were ever to go missing
                opts.Durability.AgentProgressPollInterval = 500.Milliseconds();
                opts.Durability.AgentProgressStallTimeout = 3.Seconds();

                // How long ApplyRestrictionsAsync waits for the store to agree before reporting the change as
                // not confirmed. Short here so the negative test is quick; the healthy path converges in ms.
                opts.Durability.AgentRestrictionConfirmationTimeout = 2.Seconds();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        _runtime = (WolverineRuntime)_host.Services.GetRequiredService<IWolverineRuntime>();

        await _runtime.Agents.KickstartHealthDetectionAsync();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        Uri[] running;
        while ((running = _runtime.Agents.AllRunningAgentUris().Where(x => x.Scheme == "fake").ToArray()).Length <
               FakeAgentFamily.Names.Length)
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"Fake agents never started; running = [{string.Join(", ", running.Select(x => x.ToString()))}]");
            }

            await _runtime.Agents.KickstartHealthDetectionAsync();
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        // One more evaluation while everything is observed running, so the pending-assignment ledger settles
        await _runtime.Agents.KickstartHealthDetectionAsync();
    }

    private async Task<HashSet<Uri>> persistedRunningAgentsAsync()
    {
        var (nodes, _) = await _runtime.Storage.Nodes.LoadNodeAgentStateAsync(TestContext.Current.CancellationToken);
        return nodes.SelectMany(x => x.ActiveAgents).ToHashSet();
    }

    [Fact]
    public async Task a_restart_whose_start_never_confirms_is_reported_rather_than_returning_quietly()
    {
        await startHostAsync();

        var broken = BrokenStartAgentFamily.AgentUri;

        // The broken agent has been offered its start by every evaluation above and failed each time, so it is
        // registered nowhere. That is the shape of a shard the daemon stopped on a poison event.
        _runtime.Agents.AllRunningAgentUris().ShouldNotContain(broken);
        (await persistedRunningAgentsAsync()).ShouldNotContain(broken);

        // Detach: nothing is running, so there is nothing to stop and nothing to be unconfirmed about
        var pause = new AgentRestrictions([]);
        pause.PauseAgent(broken);
        await _runtime.Agents.ApplyRestrictionsAsync(pause, CancellationToken.None);
        _runtime.Restrictions.FindPausedAgentUris().ShouldContain(broken);

        // Release. The leader offers the start again, the node fails it, nothing confirms. Before the fix this
        // returned normally and the caller had no way to tell it from a restart that worked.
        var release = new AgentRestrictions([]);
        release.Current = [new AgentRestriction(Guid.NewGuid(), broken, AgentRestrictionType.None, 0)];

        var exception = await Should.ThrowAsync<AgentRestrictionsNotConfirmedException>(() =>
            _runtime.Agents.ApplyRestrictionsAsync(release, CancellationToken.None));

        exception.Agents.ShouldContain(broken);
        exception.Message.ShouldContain(broken.ToString());

        // The restriction change itself was still persisted: the operator's intent is not lost, the leader
        // keeps re-evaluating, and the caller has been told the truth about the current state.
        _runtime.Restrictions.FindPausedAgentUris().ShouldNotContain(broken);
    }

    [Fact]
    public async Task a_pause_and_restart_that_take_effect_do_not_throw()
    {
        await startHostAsync();

        var uri = new Uri("fake://one");
        (await persistedRunningAgentsAsync()).ShouldContain(uri, "Precondition: the fake agent is running and persisted");

        var pause = new AgentRestrictions([]);
        pause.PauseAgent(uri);
        await _runtime.Agents.ApplyRestrictionsAsync(pause, CancellationToken.None);

        _runtime.Agents.AllRunningAgentUris().ShouldNotContain(uri);
        (await persistedRunningAgentsAsync()).ShouldNotContain(uri);

        var release = new AgentRestrictions([]);
        release.Current = [new AgentRestriction(Guid.NewGuid(), uri, AgentRestrictionType.None, 0)];
        await _runtime.Agents.ApplyRestrictionsAsync(release, CancellationToken.None);

        _runtime.Agents.AllRunningAgentUris().ShouldContain(uri);
        (await persistedRunningAgentsAsync()).ShouldContain(uri);
    }
}

/// <summary>
/// One agent whose start always throws: the leader can assign it, the node can never confirm it.
/// </summary>
public class BrokenStartAgentFamily : IStaticAgentFamily
{
    public static readonly Uri AgentUri = new("broken://one");

    public string Scheme => "broken";

    public ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
    {
        return ValueTask.FromResult((IReadOnlyList<Uri>)[AgentUri]);
    }

    public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
    {
        return new ValueTask<IAgent>(new BrokenStartAgent(uri));
    }

    public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
    {
        return ValueTask.FromResult((IReadOnlyList<Uri>)[AgentUri]);
    }

    public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments)
    {
        assignments.DistributeEvenly(Scheme);
        return new ValueTask();
    }

    private class BrokenStartAgent : IAgent
    {
        public BrokenStartAgent(Uri uri)
        {
            Uri = uri;
        }

        public Uri Uri { get; }

        public AgentStatus Status => AgentStatus.Stopped;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Bug4871: this agent never starts");
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
