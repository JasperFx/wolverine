using CoreTests.Transports;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// A rolling deploy brings pods up one at a time, and the leader assigns agents against whatever nodes
/// exist at that instant, so every pod that joins afterwards triggers another rebalance. On a large agent
/// universe each of those waves is thousands of agent starts and stops.
/// <see cref="DurabilitySettings.AssignmentSettlePeriod" /> lets the leader wait for the node set to go
/// quiet first, <see cref="DurabilitySettings.AssignmentSettleNodeCount" /> ends that wait as soon as the
/// deployment is fully up, and <see cref="DurabilitySettings.MaxAssignmentSettleTime" /> bounds it. A node
/// that LEAVES is never waited out — its agents are running nowhere.
/// </summary>
public class waiting_for_the_node_set_to_settle
{
    private readonly WolverineOptions _options;
    private readonly INodeAgentPersistence _persistence;
    private readonly NodeAgentController _controller;
    private readonly FrozenClock _clock = new(new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero));

    public waiting_for_the_node_set_to_settle()
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

        _persistence = Substitute.For<INodeAgentPersistence>();
        _persistence.HasLeadershipLock().Returns(false);
        _persistence.TryAttainLeadershipLockAsync(Arg.Any<CancellationToken>()).Returns(true);

        _controller = new NodeAgentController(
            runtime,
            _persistence,
            Array.Empty<IAgentFamily>(),
            NullLogger<NodeAgentController>.Instance,
            CancellationToken.None) { TimeProvider = _clock };
    }

    private WolverineNode Row(Guid nodeId, int number) => new()
    {
        NodeId = nodeId,
        AssignedNodeNumber = number,
        ControlUri = new Uri("fake://node" + number),
        LastHealthCheck = DateTimeOffset.UtcNow
    };

    private WolverineNode Self() => Row(_options.UniqueNodeId, _options.Durability.AssignedNodeNumber);

    private void ClusterIs(params WolverineNode[] nodes)
    {
        foreach (var node in nodes)
        {
            node.LastHealthCheck = DateTimeOffset.UtcNow;
        }

        _persistence.LoadNodeAgentStateAsync(Arg.Any<CancellationToken>())
            .Returns(new NodeAgentState(nodes, new AgentRestrictions()));
    }

    [Fact]
    public async Task assigns_on_the_first_tick_when_the_settle_period_is_off()
    {
        // The shipped default. Anything else would change the startup behavior of every existing app.
        _options.Durability.AssignmentSettlePeriod.ShouldBe(TimeSpan.Zero);

        ClusterIs(Self());

        await _controller.DoHealthChecksAsync();

        _controller.IsLeader.ShouldBeTrue();
        _controller.LastAssignments.ShouldNotBeNull(
            "the leader must assign immediately when no settle period is configured");
    }

    [Fact]
    public async Task holds_the_first_assignment_back_while_the_node_set_is_still_new()
    {
        _options.Durability.AssignmentSettlePeriod = 30.Seconds();

        ClusterIs(Self());

        await _controller.DoHealthChecksAsync();

        _controller.IsLeader.ShouldBeTrue("leadership is still taken — only the assignment pass waits");
        _controller.LastAssignments.ShouldBeNull();
    }

    [Fact]
    public async Task assigns_once_the_node_set_has_been_quiet_for_the_settle_period()
    {
        _options.Durability.AssignmentSettlePeriod = 30.Seconds();

        var second = Row(Guid.NewGuid(), 2);

        ClusterIs(Self());
        await _controller.DoHealthChecksAsync();
        _controller.LastAssignments.ShouldBeNull();

        // The second pod of the rollout arrives 10 seconds in, which restarts the wait rather than
        // triggering the rebalance it would have triggered before.
        _clock.Advance(10.Seconds());
        ClusterIs(Self(), second);
        await _controller.DoHealthChecksAsync();
        _controller.LastAssignments.ShouldBeNull();

        _clock.Advance(20.Seconds());
        await _controller.DoHealthChecksAsync();
        _controller.LastAssignments.ShouldBeNull("the wait restarted when the second node joined");

        _clock.Advance(10.Seconds());
        await _controller.DoHealthChecksAsync();
        _controller.LastAssignments.ShouldNotBeNull();
        _controller.LastAssignments!.Nodes.Count.ShouldBe(2);
    }

    [Fact]
    public async Task assigns_immediately_when_a_node_leaves()
    {
        _options.Durability.AssignmentSettlePeriod = 30.Seconds();

        var second = Row(Guid.NewGuid(), 2);

        ClusterIs(Self(), second);
        await _controller.DoHealthChecksAsync();
        _controller.LastAssignments.ShouldBeNull();

        // A node whose agents are running nowhere is the one case that must never wait.
        _clock.Advance(5.Seconds());
        ClusterIs(Self());
        await _controller.DoHealthChecksAsync();

        _controller.LastAssignments.ShouldNotBeNull(
            "a departure must be acted on without waiting out the settle period");
    }

    [Fact]
    public async Task gives_up_waiting_after_the_maximum_settle_time()
    {
        _options.Durability.AssignmentSettlePeriod = 30.Seconds();
        _options.Durability.MaxAssignmentSettleTime = 45.Seconds();

        var nodes = new List<WolverineNode> { Self() };
        ClusterIs(nodes.ToArray());
        await _controller.DoHealthChecksAsync();
        _controller.LastAssignments.ShouldBeNull();

        // A cluster that gains a node on every tick never goes quiet on its own.
        for (var i = 2; i <= 4; i++)
        {
            _clock.Advance(20.Seconds());
            nodes.Add(Row(Guid.NewGuid(), i));
            ClusterIs(nodes.ToArray());
            await _controller.DoHealthChecksAsync();
        }

        _controller.LastAssignments.ShouldNotBeNull(
            "MaxAssignmentSettleTime must serve a cluster that never settles");
    }

    [Fact]
    public async Task assigns_as_soon_as_the_expected_node_count_is_live()
    {
        _options.Durability.AssignmentSettlePeriod = 30.Seconds();
        _options.Durability.AssignmentSettleNodeCount = 3;

        var second = Row(Guid.NewGuid(), 2);
        var third = Row(Guid.NewGuid(), 3);

        ClusterIs(Self(), second);
        await _controller.DoHealthChecksAsync();
        _controller.LastAssignments.ShouldBeNull("the deployment is not fully up yet");

        // The last replica lands well inside the settle period. There is nothing left to wait for.
        _clock.Advance(2.Seconds());
        ClusterIs(Self(), second, third);
        await _controller.DoHealthChecksAsync();

        _controller.LastAssignments.ShouldNotBeNull();
        _controller.LastAssignments!.Nodes.Count.ShouldBe(3);
    }

    [Fact]
    public async Task a_leader_taking_over_from_a_departed_leader_assigns_immediately()
    {
        _options.Durability.AssignmentSettlePeriod = 30.Seconds();

        var oldLeader = Row(Guid.NewGuid(), 2);

        // This node runs as a follower first. Membership is tracked whatever the role, which is what lets
        // it recognise the takeover below as a node LEAVING rather than as a cluster that is still coming up.
        _persistence.TryAttainLeadershipLockAsync(Arg.Any<CancellationToken>()).Returns(false);
        ClusterIs(Self(), oldLeader);
        await _controller.DoHealthChecksAsync();
        _controller.IsLeader.ShouldBeFalse();

        // The leader dies and this node wins the election. Its agents are running nowhere.
        _clock.Advance(5.Seconds());
        _persistence.TryAttainLeadershipLockAsync(Arg.Any<CancellationToken>()).Returns(true);
        ClusterIs(Self());
        await _controller.DoHealthChecksAsync();

        _controller.IsLeader.ShouldBeTrue();
        _controller.LastAssignments.ShouldNotBeNull(
            "a leader inheriting a dead leader's agents must not wait for the node set to settle");
    }

    private sealed class FrozenClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
