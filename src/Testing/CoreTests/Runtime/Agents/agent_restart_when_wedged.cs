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
/// Regression coverage for GH-3519. A projection / event-subscription shard that starts and then dies
/// underneath its wrapper (a lost first-assignment start race that wedged at the daemon, a daemon-side
/// stop, or an execution-loop fault) used to stay registered on the node reporting a stale Running.
/// NodeAgentController.StartAgentAsync short-circuited on the bare ContainsKey, so the recurring Solo
/// reevaluation never resurrected it -- it just sat in the observable 30s retry loop forever. The
/// controller must now notice a registered-but-Stopped agent and re-drive its start.
/// </summary>
public class agent_restart_when_wedged
{
    private readonly WolverineOptions _options;
    private readonly IWolverineRuntime _runtime;
    private readonly CancellationTokenSource _cancellation = new();

    public agent_restart_when_wedged()
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
        _runtime.Observer.Returns(Substitute.For<IWolverineObserver>());
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
            Substitute.For<INodeAgentPersistence>(),
            [family],
            NullLogger<NodeAgentController>.Instance,
            _cancellation.Token);
    }

    [Fact]
    public async Task restarts_a_registered_agent_whose_shard_has_stopped_underneath_it()
    {
        var uri = new Uri("test-family://wedged");
        var agent = new FakeAgent(uri);
        var controller = controllerFor(agent);

        await controller.StartAgentAsync(uri);
        agent.StartCount.ShouldBe(1);
        controller.Agents.ContainsKey(uri).ShouldBeTrue();

        // Simulate the shard dying underneath the wrapper: the agent is still registered on the node,
        // but its underlying status has flipped to Stopped (what EventSubscriptionAgent.Status now
        // surfaces from the live daemon shard).
        agent.SimulateShardStopped();

        await controller.StartAgentAsync(uri);

        // Before the fix this second call short-circuited on ContainsKey and left the shard dead. Now
        // the wedged agent is stopped, evicted, and re-driven.
        agent.StartCount.ShouldBe(2);
        agent.Status.ShouldBe(AgentStatus.Running);
        controller.Agents.ContainsKey(uri).ShouldBeTrue();
    }

    [Fact]
    public async Task does_not_restart_a_genuinely_running_agent()
    {
        var uri = new Uri("test-family://healthy");
        var agent = new FakeAgent(uri);
        var controller = controllerFor(agent);

        await controller.StartAgentAsync(uri);
        await controller.StartAgentAsync(uri);
        await controller.StartAgentAsync(uri);

        // A running agent stays idempotent -- no needless stop/start churn.
        agent.StartCount.ShouldBe(1);
        agent.StopCount.ShouldBe(0);
    }

    [Fact]
    public async Task leaves_a_paused_agent_alone()
    {
        var uri = new Uri("test-family://paused");
        var agent = new FakeAgent(uri);
        var controller = controllerFor(agent);

        await controller.StartAgentAsync(uri);

        // Paused is a deliberate error-backoff / blue-green state that owns its own resume schedule.
        agent.SimulatePaused();
        await controller.StartAgentAsync(uri);

        agent.StartCount.ShouldBe(1);
        agent.StopCount.ShouldBe(0);
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

        // Flip the underlying status without touching the controller registration, mimicking a daemon
        // shard that stops out from under the wrapper.
        public void SimulateShardStopped() => Status = AgentStatus.Stopped;
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
