using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using Marten.Events.Daemon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using JasperFx.Resources;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Runtime.Metrics;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;
using Xunit;
namespace PersistenceTests.Agents;

/// <summary>
/// GH-3666: ApplyRestrictionsAsync used to kickstart a health-check evaluation BEFORE merging and
/// persisting the operator's restriction change, so that evaluation ran against the stale persisted
/// restrictions and could act against the operator's intent — for a pause of an agent the grid saw as
/// unassigned, it briefly STARTED the very agent being paused (observed in the GH-3663 repro), only for
/// the merged evaluation one beat later to stop it again. The restriction must be persisted first so
/// every evaluation that runs — the kickstart's included — sees the operator's intent.
/// </summary>
public class Bug_3666_pause_must_not_start_the_agent_being_paused : IAsyncDisposable
{
    private readonly ITestOutputHelper _output;
    private IHost? _host;

    public Bug_3666_pause_must_not_start_the_agent_being_paused(ITestOutputHelper output)
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

    [Fact]
    public async Task pausing_a_known_but_not_running_agent_never_starts_it()
    {
        using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync("bug3666");
            await conn.CloseAsync();
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IAgentFamily, FakeAgentFamily>();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "bug3666");
                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(_output));
                opts.Services.AddResourceSetupOnStartup();

                opts.Durability.Mode = DurabilityMode.Balanced;

                // Keep the background health-check loop silent for the duration of the test so the ONLY
                // evaluations that run are the ones ApplyRestrictionsAsync itself drives — the initial
                // startup evaluation still assigns the fake agents. This is what makes the pre-fix
                // misbehavior deterministic instead of a race against the polling loop.
                opts.Durability.HealthCheckPollingTime = 1.Hours();
                opts.Durability.CheckAssignmentPeriod = 1.Hours();
            }).StartAsync();

        var runtime = (WolverineRuntime)_host.Services.GetRequiredService<IWolverineRuntime>();

        // With the polling loop silenced, drive the first election + assignment evaluation explicitly,
        // then wait for the fake agents to be running on this single node.
        await runtime.Agents.KickstartHealthDetectionAsync();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        Uri[] running;
        while ((running = runtime.Agents.AllRunningAgentUris().Where(x => x.Scheme == "fake").ToArray()).Length <
               FakeAgentFamily.Names.Length)
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"Fake agents never started; running = [{string.Join(", ", running.Select(x => x.ToString()))}]");
            }

            await runtime.Agents.KickstartHealthDetectionAsync();
            await Task.Delay(250);
        }

        // One more evaluation while every agent is observed running, so the GH-3665
        // pending-assignment ledger confirms and clears its entries. Without this the ledger would
        // suppress the kickstarted re-assign below and mask the very churn this test pins.
        await runtime.Agents.KickstartHealthDetectionAsync();

        // Stage GH-3666's precondition: an agent the grid knows about but that is NOT currently
        // running and has NO persisted restriction. Pre-fix, the pause's kickstart evaluation saw
        // exactly this shape (stale restrictions, unassigned agent) and re-assigned it.
        var uri = running.First();
        await runtime.Agents.StopLocallyAsync(uri);
        runtime.Agents.AllRunningAgentUris().ShouldNotContain(uri);

        var recorder = new RecordingObserver(runtime.Observer);
        runtime.Observer = recorder;

        var restrictions = new AgentRestrictions([]);
        restrictions.PauseAgent(uri);
        await runtime.Agents.ApplyRestrictionsAsync(restrictions, CancellationToken.None);

        // The kickstart's commands cascade through the message bus asynchronously, so give any
        // pre-fix churn a moment to surface before asserting silence.
        await Task.Delay(2.Seconds());

        recorder.StartedAgents.ShouldNotContain(uri,
            "pausing an agent must never start it — the kickstarted evaluation ran before the " +
            "restriction was persisted and re-assigned the agent being paused");
        runtime.Agents.AllRunningAgentUris().ShouldNotContain(uri);
        runtime.Restrictions.FindPausedAgentUris().ShouldContain(uri);
    }

    /// <summary>
    /// Forwarding decorator that records AgentStarted notifications. Hand-rolled rather than a
    /// substitute because most IWolverineObserver members are not default-implemented.
    /// </summary>
    private sealed class RecordingObserver : IWolverineObserver
    {
        private readonly IWolverineObserver _inner;

        public RecordingObserver(IWolverineObserver inner)
        {
            _inner = inner;
        }

        public ConcurrentBag<Uri> StartedAgents { get; } = new();

        public Task AgentStarted(Uri agentUri)
        {
            StartedAgents.Add(agentUri);
            return _inner.AgentStarted(agentUri);
        }

        public Task AssumedLeadership() => _inner.AssumedLeadership();
        public Task LostLeadership() => _inner.LostLeadership();
        public Task NodeStarted() => _inner.NodeStarted();
        public Task NodeStopped() => _inner.NodeStopped();
        public Task AgentStopped(Uri agentUri) => _inner.AgentStopped(agentUri);
        public Task AgentPaused(Uri agentUri, ShardFailure? failure) => _inner.AgentPaused(agentUri, failure);

        public Task AssignmentsChanged(AssignmentGrid grid, AgentCommands commands) =>
            _inner.AssignmentsChanged(grid, commands);

        public Task StaleNodes(IReadOnlyList<WolverineNode> staleNodes) => _inner.StaleNodes(staleNodes);
        public Task RuntimeIsFullyStarted() => _inner.RuntimeIsFullyStarted();
        public void EndpointAdded(Endpoint endpoint) => _inner.EndpointAdded(endpoint);
        public void MessageRouted(Type messageType, IMessageRouter router) => _inner.MessageRouted(messageType, router);

        public Task BackPressureTriggered(Endpoint endpoint, IListeningAgent agent) =>
            _inner.BackPressureTriggered(endpoint, agent);

        public Task BackPressureLifted(Endpoint endpoint) => _inner.BackPressureLifted(endpoint);
        public Task ListenerLatched(Endpoint endpoint) => _inner.ListenerLatched(endpoint);

        public Task CircuitBreakerTripped(Endpoint endpoint, CircuitBreakerOptions options) =>
            _inner.CircuitBreakerTripped(endpoint, options);

        public Task CircuitBreakerReset(Endpoint endpoint) => _inner.CircuitBreakerReset(endpoint);
        public void PersistedCounts(Uri storeUri, PersistedCounts counts) => _inner.PersistedCounts(storeUri, counts);

        public void MessageHandlingMetricsExported(MessageHandlingMetrics metrics) =>
            _inner.MessageHandlingMetricsExported(metrics);
    }
}
