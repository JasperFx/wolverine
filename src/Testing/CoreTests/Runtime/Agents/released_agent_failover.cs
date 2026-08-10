using JasperFx;
using JasperFx.Core;
using JasperFx.Events.Daemon;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// Coverage for GH-3888: an event-subscription agent whose node-local auto-restart budget is exhausted
/// (see <c>stalled_agent_auto_restart</c> for the agent-side half) is RELEASED by the failed-agent
/// sweep so the leader can place it on a healthy peer advertising the same capability — instead of
/// being retried on the same sick node forever.
///
/// The field failure this pins: a node in a memory-starved state keeps writing heartbeats (the
/// heartbeat loop is deliberately cheap and isolated, GH-3604/D1), so it never looks stale to its
/// peers, and the node-local restart loop was the only recovery its stalled shards ever got. 53 shards
/// of a shared projection version froze for sixteen minutes next to a healthy fleet advertising the
/// same capability, until a manual pod restart.
///
/// The anti-bounce half: a released agent's URI goes under a capability embargo — the node re-registers
/// itself WITHOUT that capability — so the leader's capability-matched distribution cannot hand the
/// agent straight back. The embargo lapses after <see cref="DurabilitySettings.AgentReleaseCooldown" />.
/// </summary>
public class released_agent_failover
{
    private readonly WolverineOptions _options;
    private readonly IWolverineRuntime _runtime;
    private readonly IWolverineObserver _observer = Substitute.For<IWolverineObserver>();
    private readonly INodeAgentPersistence _persistence = Substitute.For<INodeAgentPersistence>();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<WolverineNode> _reregistrations = new();

    private static readonly Uri AgentUri = new("event-subscriptions://marten/invoices/all/v16/01009333");

    public released_agent_failover()
    {
        _options = new WolverineOptions { ApplicationAssembly = GetType().Assembly };
        _options.Durability.Mode = DurabilityMode.Solo;
        _options.Durability.DurabilityAgentEnabled = false;
        _options.Durability.CheckAssignmentPeriod = 1.Hours();

        _runtime = Substitute.For<IWolverineRuntime>();
        _runtime.Options.Returns(_options);
        _runtime.DurabilitySettings.Returns(_options.Durability);
        _runtime.Observer.Returns(_observer);

        // The node's own row exists throughout — otherwise every heartbeat write takes the GH-3604/D2
        // resurrection path and re-registers, polluting the capture below.
        _persistence.MarkHealthCheckAsync(Arg.Any<WolverineNode>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Capture every node-row rewrite so the tests can assert on the advertised capability set.
        _persistence
            .When(x => x.ReregisterNodeAsync(Arg.Any<WolverineNode>(), Arg.Any<CancellationToken>()))
            .Do(call => _reregistrations.Add(call.Arg<WolverineNode>()));
    }

    private void nodeStateHas(params WolverineNode[] peers)
    {
        _persistence.LoadNodeAgentStateAsync(Arg.Any<CancellationToken>())
            .Returns(new NodeAgentState(peers.ToList(), new AgentRestrictions([])));
    }

    private static WolverineNode peer(bool advertisesAgent, TimeSpan? heartbeatAge = null)
    {
        var node = new WolverineNode
        {
            NodeId = Guid.NewGuid(),
            AssignedNodeNumber = 2,
            LastHealthCheck = DateTimeOffset.UtcNow.Subtract(heartbeatAge ?? TimeSpan.Zero)
        };

        if (advertisesAgent)
        {
            node.Capabilities.Add(AgentUri);
        }

        return node;
    }

    private async Task<(NodeAgentController Controller, ReleasableFakeAgent Agent)> controllerWithExhaustedAgentAsync()
    {
        var agent = new ReleasableFakeAgent(AgentUri);
        var family = new ReleasableFakeAgentFamily("event-subscriptions");
        family.Add(agent);

        var controller = new NodeAgentController(_runtime, _persistence, [family],
            NullLogger<NodeAgentController>.Instance, _cancellation.Token);

        // Captures the node's advertised capabilities (this is what a real Balanced node does at
        // startup), so releasing can be observed as a capability set that shrinks and later grows back.
        await controller.StartLocalAgentProcessingAsync(_options);

        await controller.StartAgentAsync(AgentUri);
        agent.SimulateExhaustedLocalRestarts();

        return (controller, agent);
    }

    [Fact]
    public async Task releases_an_exhausted_agent_when_a_live_peer_advertises_the_capability()
    {
        var (controller, agent) = await controllerWithExhaustedAgentAsync();
        nodeStateHas(peer(advertisesAgent: true));

        await controller.ReportFailedLocalAgentsAsync();

        // The agent let go locally: stopped, deregistered, and its assignment row removed so the next
        // leader evaluation sees it unassigned.
        agent.StopCount.ShouldBe(1);
        controller.Agents.ContainsKey(AgentUri).ShouldBeFalse();
        await _persistence.Received(1)
            .RemoveAssignmentAsync(_options.UniqueNodeId, AgentUri, Arg.Any<CancellationToken>());

        // The anti-bounce half, persisted BEFORE the agent is let go: this node's row no longer
        // advertises the capability, so the leader cannot hand the agent straight back.
        _reregistrations.ShouldNotBeEmpty();
        _reregistrations.Last().Capabilities.ShouldNotContain(AgentUri);

        await _observer.Received(1).AgentReleased(AgentUri, Arg.Any<ShardFailure?>());
    }

    [Fact]
    public async Task keeps_local_retries_when_no_live_peer_advertises_the_capability()
    {
        var (controller, agent) = await controllerWithExhaustedAgentAsync();

        // A live peer exists, but it cannot run this agent. Releasing would strand the shard entirely,
        // so the sweep declines, refunds the local restart budget, and the agent stays put.
        nodeStateHas(peer(advertisesAgent: false));

        await controller.ReportFailedLocalAgentsAsync();

        agent.StopCount.ShouldBe(0);
        agent.BudgetResets.ShouldBe(1);
        controller.Agents.ContainsKey(AgentUri).ShouldBeTrue();
        _reregistrations.ShouldBeEmpty();
        await _observer.DidNotReceive().AgentReleased(Arg.Any<Uri>(), Arg.Any<ShardFailure?>());
    }

    [Fact]
    public async Task a_stale_peer_advertising_the_capability_does_not_count()
    {
        var (controller, agent) = await controllerWithExhaustedAgentAsync();

        // The only capable peer stopped heartbeating long ago — releasing to it would freeze the shard
        // in a different place. Same outcome as having no capable peer at all.
        nodeStateHas(peer(advertisesAgent: true, heartbeatAge: 10.Minutes()));

        await controller.ReportFailedLocalAgentsAsync();

        agent.StopCount.ShouldBe(0);
        agent.BudgetResets.ShouldBe(1);
        await _observer.DidNotReceive().AgentReleased(Arg.Any<Uri>(), Arg.Any<ShardFailure?>());
    }

    [Fact]
    public async Task the_capability_is_advertised_again_after_the_release_cooldown()
    {
        var clock = new FrozenClock(DateTimeOffset.UtcNow);
        var (controller, _) = await controllerWithExhaustedAgentAsync();
        controller.TimeProvider = clock;
        nodeStateHas(peer(advertisesAgent: true));

        await controller.ReportFailedLocalAgentsAsync();
        _reregistrations.Last().Capabilities.ShouldNotContain(AgentUri);

        // Before the cooldown lapses, the embargo holds.
        await controller.RestoreExpiredReleaseEmbargoesAsync();
        _reregistrations.Last().Capabilities.ShouldNotContain(AgentUri);

        // After it lapses, the node advertises the capability again and becomes an ordinary candidate.
        clock.Advance(_options.Durability.AgentReleaseCooldown + 1.Minutes());
        await controller.RestoreExpiredReleaseEmbargoesAsync();
        _reregistrations.Last().Capabilities.ShouldContain(AgentUri);
    }

    [Fact]
    public async Task release_report_fires_once_not_once_per_tick_when_declined()
    {
        var (controller, agent) = await controllerWithExhaustedAgentAsync();
        nodeStateHas(peer(advertisesAgent: false));

        await controller.ReportFailedLocalAgentsAsync();

        // ResetLocalRestartBudget cleared the exhaustion, so subsequent sweeps see an ordinary agent
        // again until it burns another full budget.
        await controller.ReportFailedLocalAgentsAsync();
        await controller.ReportFailedLocalAgentsAsync();

        agent.BudgetResets.ShouldBe(1);
    }

    /// <summary>
    /// The placement half of the story, at the assignment-grid level: once the releasing node stops
    /// advertising the capability, the blue/green distribution places the freed agent on the peer that
    /// does advertise it — never back on the node that just failed it.
    /// </summary>
    public class released_agent_placement
    {
        [Fact]
        public void a_released_agent_lands_on_the_capable_peer_not_back_on_the_releasing_node()
        {
            var shared = new Uri("event-subscriptions://marten/incidents/all");

            var sick = new WolverineNode { NodeId = Guid.NewGuid(), AssignedNodeNumber = 1 };
            sick.Capabilities.Add(shared); // released AgentUri: no longer advertised here

            var healthy = new WolverineNode { NodeId = Guid.NewGuid(), AssignedNodeNumber = 2 };
            healthy.Capabilities.Add(shared);
            healthy.Capabilities.Add(AgentUri);

            var grid = new AssignmentGrid();
            grid.WithNode(sick);
            grid.WithNode(healthy);
            grid.WithAgents(AgentUri, shared);

            grid.DistributeEvenlyWithBlueGreenSemantics("event-subscriptions");

            grid.AgentFor(AgentUri).AssignedNode.ShouldNotBeNull().AssignedId.ShouldBe(2);
        }
    }

    private sealed class FrozenClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private class ReleasableFakeAgentFamily : IStaticAgentFamily
    {
        private readonly Dictionary<Uri, ReleasableFakeAgent> _agents = new();

        public ReleasableFakeAgentFamily(string scheme) => Scheme = scheme;

        public void Add(ReleasableFakeAgent agent) => _agents[agent.Uri] = agent;

        public string Scheme { get; }

        public ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
            => ValueTask.FromResult<IReadOnlyList<Uri>>(_agents.Keys.ToList());

        public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
            => ValueTask.FromResult<IAgent>(_agents[uri]);

        public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
            => ValueTask.FromResult<IReadOnlyList<Uri>>(_agents.Keys.ToList());

        public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments) => ValueTask.CompletedTask;
    }

    private class ReleasableFakeAgent : IEventSubscriptionAgent
    {
        public ReleasableFakeAgent(Uri uri) => Uri = uri;

        public int StopCount { get; private set; }
        public int BudgetResets { get; private set; }

        public Uri Uri { get; }
        public AgentStatus Status { get; private set; } = AgentStatus.Stopped;
        public ShardFailure? Failure { get; private set; }
        public bool LocalRestartsExhausted { get; private set; }

        public void SimulateExhaustedLocalRestarts()
        {
            // A stalled shard still reads Running — that is precisely why the sweep must check the
            // exhaustion flag before the Running short-circuit.
            Status = AgentStatus.Running;
            Failure = new ShardFailure
            {
                Category = ShardFailureCategory.Other,
                ExceptionType = "System.OutOfMemoryException",
                RootExceptionType = "System.OutOfMemoryException",
                Message = "Exception of type 'System.OutOfMemoryException' was thrown.",
                Detail = "System.OutOfMemoryException: Exception of type 'System.OutOfMemoryException' was thrown.",
                OccurredAt = DateTimeOffset.UtcNow
            };
            LocalRestartsExhausted = true;
        }

        public void ResetLocalRestartBudget()
        {
            BudgetResets++;
            LocalRestartsExhausted = false;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Status = AgentStatus.Running;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            Status = AgentStatus.Stopped;
            return Task.CompletedTask;
        }

        public Task RebuildAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RewindAsync(long? sequenceFloor, DateTimeOffset? timestamp,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
