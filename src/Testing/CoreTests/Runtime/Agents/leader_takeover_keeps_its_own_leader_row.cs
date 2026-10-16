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
///     A leader crashes hard. Its row is still inside StaleNodeTimeout when a survivor wins the advisory lock, so
///     the survivor's takeover snapshot still shows the dead predecessor owning wolverine://leader. The takeover
///     upserts the leader row to the survivor, and adds LeaderUri to the survivor's copy of the snapshot WITHOUT
///     taking it off the predecessor's -- so the grid sees the leader "running" on two nodes, and the GH-2602
///     duplicate healer stops the copy on whichever node was enumerated first. When that is the survivor, the
///     stop runs locally and its owner-scoped RemoveAssignmentAsync deletes the row the survivor just wrote:
///     the lock is held, IsLeader is true, nothing ever rewrites the row, and every observer reads "no leader"
///     until the next election.
/// </summary>
public class leader_takeover_keeps_its_own_leader_row
{
    private readonly WolverineOptions _options;
    private readonly INodeAgentPersistence _persistence;
    private readonly IWolverineRuntime _runtime;
    private readonly NodeAgentController _controller;

    public leader_takeover_keeps_its_own_leader_row()
    {
        _options = new WolverineOptions
        {
            ApplicationAssembly = GetType().Assembly
        };
        _options.Transports.NodeControlEndpoint = new FakeEndpoint("fake://self".ToUri(), EndpointRole.System);
        _options.Durability.DurabilityAgentEnabled = false;
        _options.Durability.StaleNodeTimeout = 1.Minutes(); // the default the field report ran with
        _options.Durability.AssignedNodeNumber = 1;

        _runtime = Substitute.For<IWolverineRuntime>();
        _runtime.Options.Returns(_options);
        _runtime.DurabilitySettings.Returns(_options.Durability);
        _runtime.Observer.Returns(Substitute.For<IWolverineObserver>());

        _persistence = Substitute.For<INodeAgentPersistence>();

        // The predecessor's advisory lock died with its connection, so the survivor wins it on this tick
        _persistence.HasLeadershipLock().Returns(false);
        _persistence.TryAttainLeadershipLockAsync(Arg.Any<CancellationToken>()).Returns(true);

        _controller = new NodeAgentController(
            _runtime,
            _persistence,
            Array.Empty<IAgentFamily>(),
            NullLogger<NodeAgentController>.Instance,
            CancellationToken.None);
    }

    private WolverineNode survivor() => new()
    {
        NodeId = _options.UniqueNodeId,
        AssignedNodeNumber = 1,
        ControlUri = _options.Transports.NodeControlEndpoint!.Uri,
        LastHealthCheck = DateTimeOffset.UtcNow
    };

    // Crashed ~12s ago: well inside the stale window, still carrying the leader assignment row
    private static WolverineNode crashedPredecessor()
    {
        var node = new WolverineNode
        {
            NodeId = Guid.NewGuid(),
            AssignedNodeNumber = 2,
            ControlUri = new Uri("fake://predecessor"),
            LastHealthCheck = DateTimeOffset.UtcNow.Subtract(12.Seconds())
        };
        node.AssignAgents([NodeAgentController.LeaderUri]);
        return node;
    }

    private async Task<AgentCommands> takeOverWithSnapshot(params WolverineNode[] nodes)
    {
        _persistence.LoadNodeAgentStateAsync(Arg.Any<CancellationToken>())
            .Returns(new NodeAgentState(nodes, new AgentRestrictions()));

        var commands = await _controller.DoHealthChecksAsync();

        _controller.IsLeader.ShouldBeTrue();
        await _persistence.Received()
            .AddAssignmentAsync(_options.UniqueNodeId, NodeAgentController.LeaderUri, Arg.Any<CancellationToken>());

        return commands;
    }

    private static IEnumerable<(Uri Agent, Guid Node)> stopsIn(AgentCommands commands)
    {
        foreach (var command in commands)
        {
            switch (command)
            {
                case StopRemoteAgent stop:
                    yield return (stop.AgentUri, stop.Destination.NodeId);
                    break;
                case StopRemoteAgents stops:
                    foreach (var uri in stops.AgentIds) yield return (uri, stops.Destination.NodeId);
                    break;
            }
        }
    }

    [Fact]
    public async Task survivor_enumerated_first_is_not_told_to_stop_its_own_leader_agent()
    {
        var commands = await takeOverWithSnapshot(survivor(), crashedPredecessor());

        stopsIn(commands).ShouldNotContain(x => x.Agent == NodeAgentController.LeaderUri,
            "the takeover must never stop the leader agent on the node that just won the lock -- the local stop deletes the leader row it just wrote");
    }

    [Fact]
    public async Task predecessor_enumerated_first_does_not_produce_a_leader_stop_either()
    {
        var commands = await takeOverWithSnapshot(crashedPredecessor(), survivor());

        // Leadership has no agent to stop: the lock and the row ARE its lifecycle. A stop aimed at the dead
        // node is a remote invoke into a lane nobody will answer; a stop aimed at self deletes the row.
        stopsIn(commands).ShouldNotContain(x => x.Agent == NodeAgentController.LeaderUri);
    }

    [Fact]
    public async Task a_sitting_leader_rewrites_its_leader_row_when_the_snapshot_no_longer_shows_it()
    {
        await takeOverWithSnapshot(survivor(), crashedPredecessor());

        // Next tick: the lease renews, the predecessor has been ejected, and the leader row is gone from the
        // table (this is what the field saw for 17 minutes). The lock holder puts the row back.
        _persistence.HasLeadershipLock().Returns(true);
        _persistence.LoadNodeAgentStateAsync(Arg.Any<CancellationToken>())
            .Returns(new NodeAgentState([survivor()], new AgentRestrictions()));

        await _controller.DoHealthChecksAsync();

        _controller.IsLeader.ShouldBeTrue();
        await _persistence.Received(2)
            .AddAssignmentAsync(_options.UniqueNodeId, NodeAgentController.LeaderUri, Arg.Any<CancellationToken>());
        _controller.LastAssignments!.Nodes.Single().IsLeader.ShouldBeTrue();
    }

    [Fact]
    public async Task a_sitting_leader_does_not_rewrite_a_leader_row_that_is_there()
    {
        await takeOverWithSnapshot(survivor(), crashedPredecessor());

        _persistence.HasLeadershipLock().Returns(true);
        var self = survivor();
        self.AssignAgents([NodeAgentController.LeaderUri]);
        _persistence.LoadNodeAgentStateAsync(Arg.Any<CancellationToken>())
            .Returns(new NodeAgentState([self], new AgentRestrictions()));

        await _controller.DoHealthChecksAsync();

        await _persistence.Received(1)
            .AddAssignmentAsync(_options.UniqueNodeId, NodeAgentController.LeaderUri, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task the_grid_knows_exactly_one_leader_after_the_takeover(bool survivorFirst)
    {
        await takeOverWithSnapshot(survivorFirst
            ? [survivor(), crashedPredecessor()]
            : [crashedPredecessor(), survivor()]);

        var leaders = _controller.LastAssignments!.Nodes.Where(x => x.IsLeader).ToArray();
        leaders.ShouldHaveSingleItem();
        leaders[0].NodeId.ShouldBe(_options.UniqueNodeId);
    }
}
