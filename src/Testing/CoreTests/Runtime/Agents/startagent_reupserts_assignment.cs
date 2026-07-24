using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// Regression coverage for the D4 half of the projection-agent churn livelock (GH-3604). When a peer
/// wipes a live node's node_assignments rows out from under it (an ejection/resurrection under churn),
/// the leader keeps re-emitting AssignAgent for the same (uri, node) pair. NodeAgentController.StartAgentAsync
/// used to short-circuit an already-running agent with a bare early return, so the assignment row was never
/// re-persisted and the grid could never re-learn what was actually running. The early return must now
/// still upsert the assignment row -- without a needless stop/start and without a second AgentStarted record.
/// </summary>
public class startagent_reupserts_assignment
{
    private readonly WolverineOptions _options;
    private readonly IWolverineRuntime _runtime;
    private readonly IWolverineObserver _observer = Substitute.For<IWolverineObserver>();
    private readonly INodeAgentPersistence _persistence = Substitute.For<INodeAgentPersistence>();
    private readonly CancellationTokenSource _cancellation = new();

    public startagent_reupserts_assignment()
    {
        _options = new WolverineOptions
        {
            ApplicationAssembly = GetType().Assembly
        };
        _options.Durability.Mode = DurabilityMode.Solo;
        _options.Durability.DurabilityAgentEnabled = false; // skip MessageStoreCollection wiring
        _options.Durability.CheckAssignmentPeriod = 1.Hours(); // keep the recurring loop out of the way

        _runtime = Substitute.For<IWolverineRuntime>();
        _runtime.Options.Returns(_options);
        _runtime.DurabilitySettings.Returns(_options.Durability);
        _runtime.Observer.Returns(_observer);
    }

    private NodeAgentController controllerFor(params FakeAgent[] agents)
    {
        var family = new FakeAgentFamily("test-family");
        foreach (var agent in agents)
        {
            family.Add(agent);
        }

        return new NodeAgentController(
            _runtime,
            _persistence,
            [family],
            NullLogger<NodeAgentController>.Instance,
            _cancellation.Token);
    }

    [Fact]
    public async Task re_upserts_assignment_for_an_already_running_agent_without_restarting_it()
    {
        var uri = new Uri("test-family://runner");
        var agent = new FakeAgent(uri);
        var controller = controllerFor(agent);

        await controller.StartAgentAsync(uri);
        agent.StartCount.ShouldBe(1);
        await _persistence.Received(1).AddAssignmentAsync(_options.UniqueNodeId, uri, Arg.Any<CancellationToken>());
        await _observer.Received(1).AgentStarted(uri);

        // Simulate a peer wiping this live node's assignment rows: the agent is still running here, but the
        // node_assignments row is gone in persistence. The re-assign that follows must put it back.
        _persistence.ClearReceivedCalls();

        await controller.StartAgentAsync(uri);

        // The already-running agent is NOT stopped/started again -- no churn, no duplicate AgentStarted...
        agent.StartCount.ShouldBe(1);
        agent.StopCount.ShouldBe(0);
        await _observer.Received(1).AgentStarted(uri);

        // ...but the assignment row IS re-upserted so the grid can re-learn what is running here.
        await _persistence.Received(1).AddAssignmentAsync(_options.UniqueNodeId, uri, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task re_upserts_assignment_for_a_paused_agent_without_resuming_it()
    {
        var uri = new Uri("test-family://paused");
        var agent = new FakeAgent(uri);
        var controller = controllerFor(agent);

        await controller.StartAgentAsync(uri);

        // Paused is a deliberate error-backoff / blue-green state; the agent owns its own resume schedule.
        agent.SimulatePaused();
        _persistence.ClearReceivedCalls();

        await controller.StartAgentAsync(uri);

        agent.StartCount.ShouldBe(1);
        agent.StopCount.ShouldBe(0);
        agent.Status.ShouldBe(AgentStatus.Paused);

        // The node still owns this (paused) agent, so its assignment must survive a re-assign wave.
        await _persistence.Received(1).AddAssignmentAsync(_options.UniqueNodeId, uri, Arg.Any<CancellationToken>());
    }

    private class FakeAgentFamily : IAgentFamily
    {
        private readonly Dictionary<Uri, FakeAgent> _agents = new();

        public FakeAgentFamily(string scheme)
        {
            Scheme = scheme;
        }

        public void Add(FakeAgent agent) => _agents[agent.Uri] = agent;

        public string Scheme { get; }

        public ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
            => ValueTask.FromResult<IReadOnlyList<Uri>>(_agents.Keys.ToList());

        public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
            => ValueTask.FromResult<IAgent>(_agents[uri]);

        public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
            => ValueTask.FromResult<IReadOnlyList<Uri>>(_agents.Keys.ToList());

        public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments) => ValueTask.CompletedTask;
    }

    private class FakeAgent : IAgent
    {
        public FakeAgent(Uri uri) => Uri = uri;

        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public Uri Uri { get; }
        public AgentStatus Status { get; private set; } = AgentStatus.Stopped;

        public void SimulatePaused() => Status = AgentStatus.Paused;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            Status = AgentStatus.Running;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            Status = AgentStatus.Stopped;
            return Task.CompletedTask;
        }
    }
}
