using CoreTests.Transports;
using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-3987: the node-side assigned-vs-running reconciliation sweep. The assignment table is keyed one
/// row per agent, so once the two facts diverge the leader is structurally blind to it: an agent whose
/// row names this node but which is not running here reads as "assigned" forever, and a copy running
/// here whose row a peer overwrote is invisible to the grid. Only the node holding the divergence can
/// see it, by comparing its own registrations against its persisted claims on the health-check tick —
/// follower-capable, because divergence does not care who the leader is.
///
/// A mismatch must be observed for LocalAgentReconciliationThreshold consecutive ticks before it is
/// acted on, so anything legitimately in flight (a start racing its own assignment row, a stop racing
/// its row delete) clears itself without churn.
/// </summary>
public class local_agent_reconciliation_sweep
{
    private readonly WolverineOptions _options;
    private readonly INodeAgentPersistence _persistence = Substitute.For<INodeAgentPersistence>();
    private readonly NodeAgentController _controller;
    private readonly FakeAgentFamily _family = new("test-family");

    public local_agent_reconciliation_sweep()
    {
        _options = new WolverineOptions
        {
            ApplicationAssembly = GetType().Assembly
        };
        _options.Transports.NodeControlEndpoint = new FakeEndpoint("fake://self".ToUri(), EndpointRole.System);
        _options.Durability.DurabilityAgentEnabled = false;

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(_options);
        runtime.DurabilitySettings.Returns(_options.Durability);
        runtime.Observer.Returns(Substitute.For<IWolverineObserver>());

        // The sweep must not depend on leadership, so this node stays a follower throughout.
        _persistence.HasLeadershipLock().Returns(false);
        _persistence.TryAttainLeadershipLockAsync(Arg.Any<CancellationToken>()).Returns(false);

        _controller = new NodeAgentController(
            runtime,
            _persistence,
            [_family],
            NullLogger<NodeAgentController>.Instance,
            CancellationToken.None);
    }

    private WolverineNode Row(Guid nodeId, int number, params Uri[] activeAgents)
    {
        var node = new WolverineNode
        {
            NodeId = nodeId,
            AssignedNodeNumber = number,
            ControlUri = new Uri("fake://node" + number)
        };
        node.ActiveAgents.AddRange(activeAgents);
        return node;
    }

    private WolverineNode Self(params Uri[] activeAgents)
        => Row(_options.UniqueNodeId, _options.Durability.AssignedNodeNumber, activeAgents);

    private void ClusterIs(params WolverineNode[] nodes) => ClusterIs(new AgentRestrictions(), nodes);

    private void ClusterIs(AgentRestrictions restrictions, params WolverineNode[] nodes)
    {
        foreach (var node in nodes)
        {
            node.LastHealthCheck = DateTimeOffset.UtcNow;
        }

        _persistence.LoadNodeAgentStateAsync(Arg.Any<CancellationToken>())
            .Returns(new NodeAgentState(nodes, restrictions));
    }

    private async Task tick(int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            await _controller.DoHealthChecksAsync();
        }
    }

    [Fact]
    public async Task starts_an_agent_that_is_durably_assigned_here_but_not_running()
    {
        var uri = new Uri("test-family://one");
        var agent = _family.Add(uri);

        // The GH-3987 wedge: the row says this node owns the agent, but nothing is running. The leader
        // reads this as converged forever; only this node can notice.
        ClusterIs(Self(uri));

        await tick(2);
        agent.StartCount.ShouldBe(0);

        await tick();
        agent.StartCount.ShouldBe(1);
    }

    [Fact]
    public async Task stops_a_local_copy_whose_durable_assignment_belongs_to_another_live_node()
    {
        var uri = new Uri("test-family://one");
        var agent = _family.Add(uri);

        await _controller.StartAgentAsync(uri);
        agent.StartCount.ShouldBe(1);

        // The leadership-handover duplicate: this copy started here, but the one-row-per-agent table
        // now names a peer (last writer wins), so this copy is an orphan nothing else will ever stop.
        var owner = Row(Guid.NewGuid(), 2, uri);
        ClusterIs(Self(), owner);

        await tick(2);
        agent.StopCount.ShouldBe(0);

        await tick();
        agent.StopCount.ShouldBe(1);
    }

    [Fact]
    public async Task reclaims_the_row_for_a_local_copy_no_row_accounts_for_instead_of_stopping_it()
    {
        var uri = new Uri("test-family://one");
        var agent = _family.Add(uri);

        await _controller.StartAgentAsync(uri);
        _persistence.ClearReceivedCalls();

        // The row vanished entirely (peer ejection cascade). Nobody else claims the agent, so the
        // running copy is the one true copy: restore the claim, never stop the work.
        ClusterIs(Self(), Row(Guid.NewGuid(), 2));

        await tick(3);

        agent.StopCount.ShouldBe(0);
        await _persistence.Received()
            .AddAssignmentAsync(_options.UniqueNodeId, uri, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task a_transient_mismatch_that_heals_on_its_own_is_never_acted_on()
    {
        var uri = new Uri("test-family://one");
        var agent = _family.Add(uri);

        // Two ticks of mismatch — one short of the threshold...
        ClusterIs(Self(uri));
        await tick(2);

        // ...then the divergence heals on its own (the in-flight start this row belonged to landed on
        // a peer and the row moved on). The streak must reset, not resume.
        ClusterIs(Self(), Row(Guid.NewGuid(), 2, uri));
        await tick();

        ClusterIs(Self(uri));
        await tick(2);

        agent.StartCount.ShouldBe(0);
    }

    [Fact]
    public async Task the_sweep_is_off_when_the_threshold_is_zero()
    {
        _options.Durability.LocalAgentReconciliationThreshold = 0;

        var uri = new Uri("test-family://one");
        var agent = _family.Add(uri);

        ClusterIs(Self(uri));
        await tick(5);

        agent.StartCount.ShouldBe(0);
    }

    [Fact]
    public async Task a_paused_agent_is_not_dragged_back_by_its_own_stale_row()
    {
        var uri = new Uri("test-family://one");
        var agent = _family.Add(uri);

        // An operator's pause outranks the row: the assignment row survives a pause on purpose (the
        // node still owns the agent), so the sweep must not read "assigned but not running" from it.
        var paused = new AgentRestrictions([
            new AgentRestriction(Guid.NewGuid(), uri, AgentRestrictionType.Paused, 0)
        ]);
        ClusterIs(paused, Self(uri));

        await tick(5);

        agent.StartCount.ShouldBe(0);
    }

    private class FakeAgentFamily : IAgentFamily
    {
        private readonly Dictionary<Uri, FakeAgent> _agents = new();

        public FakeAgentFamily(string scheme)
        {
            Scheme = scheme;
        }

        public FakeAgent Add(Uri uri)
        {
            var agent = new FakeAgent(uri);
            _agents[uri] = agent;
            return agent;
        }

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
