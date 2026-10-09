using CoreTests.Transports;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-3698. Once the leader's assignment evaluation stopped waiting for the agent command drain, an agent
/// that had been dispatched but not yet confirmed running looked completely unplaced on the next pass —
/// <c>Agent.OriginalNode</c> comes from each node's PERSISTED active agents, and the assignment row only
/// appears once the agent is actually running. The pass therefore re-decided its placement and emitted a
/// plain <c>AssignAgent</c> to a second node, with no stop for the copy already starting on the first, and
/// the same agent ran on two nodes at once.
///
/// <para>The fix makes a pending dispatch a state the grid can see (<c>Agent.PendingNode</c>) rather than a
/// list of commands filtered out afterwards, so every outcome accounts for the copy that may be coming up:
/// a stop, or a stop-then-start ordered through that node's own lane — never a bare start elsewhere.</para>
/// </summary>
public class pending_assignment_grid_state
{
    private readonly WolverineOptions _options;
    private readonly IWolverineRuntime _runtime;
    private readonly INodeAgentPersistence _persistence = Substitute.For<INodeAgentPersistence>();
    private readonly FakeAgentFamily _family = new("fake");
    private readonly NodeAgentController _controller;
    private readonly WolverineNode _node1;
    private readonly WolverineNode _node2;

    public pending_assignment_grid_state()
    {
        _options = new WolverineOptions { ApplicationAssembly = GetType().Assembly };
        _options.Transports.NodeControlEndpoint = new FakeEndpoint("fake://self".ToUri(), EndpointRole.System);
        _options.Durability.DurabilityAgentEnabled = false;

        _runtime = Substitute.For<IWolverineRuntime>();
        _runtime.Options.Returns(_options);
        _runtime.DurabilitySettings.Returns(_options.Durability);
        _runtime.Observer.Returns(Substitute.For<IWolverineObserver>());

        _controller = new NodeAgentController(
            _runtime, _persistence, [_family], NullLogger<NodeAgentController>.Instance, CancellationToken.None);

        _node1 = nodeFor(_options.UniqueNodeId, 1, "fake://self");
        _node2 = nodeFor(Guid.NewGuid(), 2, "fake://two");
    }

    private WolverineNode nodeFor(Guid id, int number, string controlUri)
    {
        var node = new WolverineNode
        {
            NodeId = id,
            AssignedNodeNumber = number,
            ControlUri = controlUri.ToUri()
        };

        node.Capabilities.AddRange(_family.AllAgentUris());
        return node;
    }

    private Task<AgentCommands> evaluateAsync(params WolverineNode[] nodes)
        => _controller.EvaluateAssignmentsAsync(nodes, new AgentRestrictions());

    // batchCommands chunks a destination's assignments into AssignAgents, so a start can arrive in either
    // shape depending on how many go to the same node.
    private static Uri[] startedAgents(AgentCommands commands)
        => commands.OfType<AssignAgent>().Select(x => x.AgentUri)
            .Concat(commands.OfType<AssignAgents>().SelectMany(x => x.AgentIds))
            .ToArray();

    // A move can arrive alone or batched with the rest of the rebalance, depending on how many agents move
    // between the same two nodes.
    private static (Uri Agent, Guid From, Guid To, Guid? Lane)[] moves(AgentCommands commands)
        => commands.OfType<ReassignAgent>()
            .Select(x => (x.AgentUri, x.OriginalNode.NodeId, x.ActiveNode.NodeId, x.DestinationNodeId))
            .Concat(commands.OfType<ReassignAgents>().SelectMany(x =>
                x.AgentUris.Select(uri => (uri, x.OriginalNode.NodeId, x.ActiveNode.NodeId, x.DestinationNodeId))))
            .ToArray();

    // Puts every agent in the "dispatched to node 1, not yet confirmed running" state the whole class is
    // about: one evaluation against a single node, whose ActiveAgents deliberately stay empty afterwards.
    private async Task dispatchEverythingToNode1Async()
    {
        var first = await evaluateAsync(_node1);
        startedAgents(first).Length.ShouldBe(FakeAgentFamily.Names.Length);
    }

    [Fact]
    public async Task a_node_joining_mid_wave_takes_its_share_of_the_agents_still_pending()
    {
        await dispatchEverythingToNode1Async();

        // The in-flight agents count toward node 1's share, so node 1 keeps exactly that and only the excess
        // moves. GH-4901: this used to come out empty, because the families that distribute after this one
        // put every pending agent back on node 1 -- a node joining mid-wave got nothing until the wave was over.
        var commands = await evaluateAsync(_node1, _node2);

        startedAgents(commands).ShouldBeEmpty();

        var moved = moves(commands);
        moved.Length.ShouldBe(FakeAgentFamily.Names.Length / 2);
        moved.ShouldAllBe(x => x.From == _node1.NodeId && x.To == _node2.NodeId);
    }

    [Fact]
    public async Task an_agent_still_being_moved_is_not_moved_again()
    {
        await dispatchEverythingToNode1Async();
        var moving = moves(await evaluateAsync(_node1, _node2)).Select(x => x.Agent).ToArray();
        moving.ShouldNotBeEmpty();

        // The moves are still on their way to node 2 when node 3 joins. GH-4901: a second move of the same agent
        // queues behind the first, finds it already gone from its source, and starts it on a second node.
        _controller.PendingDispatches = (Uri uri, out Guid nodeId) =>
        {
            nodeId = moving.Contains(uri) ? _node2.NodeId : _node1.NodeId;
            return true;
        };

        var node3 = nodeFor(Guid.NewGuid(), 3, "fake://three");
        var commands = await evaluateAsync(_node1, _node2, node3);

        moves(commands).Select(x => x.Agent).ShouldNotContain(x => moving.Contains(x));
        startedAgents(commands).ShouldNotContain(x => moving.Contains(x));
    }

    [Fact]
    public async Task moving_a_pending_agent_never_emits_a_bare_start_on_the_new_node()
    {
        await dispatchEverythingToNode1Async();

        // An operator pin outranks a dispatch the leader has not managed to complete, so this is a move of
        // an agent node 1 may be starting at this very moment.
        var pinned = _family.AllAgentUris().First();
        var restrictions = new AgentRestrictions();
        restrictions.PinAgent(pinned, _node2.AssignedNodeNumber);

        var commands = await _controller.EvaluateAssignmentsAsync([_node1, _node2], restrictions);

        // THE regression. Pre-fix the move was a bare start naming node 2 and nothing else — node 1 was
        // never told to let go, and both nodes ended up running the agent.
        startedAgents(commands).ShouldNotContain(pinned);

        var reassign = moves(commands).Single(x => x.Agent == pinned);
        reassign.From.ShouldBe(_node1.NodeId);
        reassign.To.ShouldBe(_node2.NodeId);
    }

    [Fact]
    public async Task the_stop_half_of_a_pending_move_is_ordered_behind_the_start_it_is_cancelling()
    {
        await dispatchEverythingToNode1Async();

        var pinned = _family.AllAgentUris().First();
        var restrictions = new AgentRestrictions();
        restrictions.PinAgent(pinned, _node2.AssignedNodeNumber);

        var commands = await _controller.EvaluateAssignmentsAsync([_node1, _node2], restrictions);

        // A reassignment runs in the lane of its SOURCE node (GH-3749). That matters doubly here: the
        // start it has to cancel is still sitting in node 1's queue, so a stop dispatched anywhere else
        // finds nothing to stop and node 1 brings the agent up moments later anyway.
        moves(commands).Single(x => x.Agent == pinned).Lane.ShouldBe(_node1.NodeId);
    }

    [Fact]
    public async Task pausing_a_pending_agent_stops_it_where_it_was_dispatched()
    {
        await dispatchEverythingToNode1Async();

        var paused = _family.AllAgentUris().First();
        var restrictions = new AgentRestrictions();
        restrictions.PauseAgent(paused);

        var commands = await _controller.EvaluateAssignmentsAsync([_node1], restrictions);

        // Pre-fix this emitted nothing at all: the agent had no OriginalNode to stop it on, so the pause
        // silently let the in-flight start come up and keep running.
        var stop = commands.OfType<StopRemoteAgent>().ShouldHaveSingleItem();
        stop.AgentUri.ShouldBe(paused);
        stop.Destination.NodeId.ShouldBe(_node1.NodeId);
    }

    [Fact]
    public async Task an_outstanding_dispatch_holds_its_assignment_past_the_ttl()
    {
        // The TTL is 2 x CheckAssignmentPeriod. A wave of slow agent starts routinely runs for minutes,
        // which is precisely why the clock cannot be what decides that a dispatch is over.
        _options.Durability.CheckAssignmentPeriod = 10.Milliseconds();

        await dispatchEverythingToNode1Async();

        var node1Id = _node1.NodeId;
        _controller.PendingDispatches = (Uri _, out Guid nodeId) =>
        {
            nodeId = node1Id;
            return true;
        };

        await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);

        // The dispatcher still has the starts queued, so nothing is re-driven and nothing is moved.
        (await evaluateAsync(_node1)).ShouldBeEmpty();
    }

    [Fact]
    public async Task a_dispatch_the_dispatcher_has_finished_with_is_retried_after_the_ttl()
    {
        _options.Durability.CheckAssignmentPeriod = 10.Milliseconds();

        await dispatchEverythingToNode1Async();

        // Lanes are done with the commands — the starts either failed or completed without the agents ever
        // turning up in the node's persisted assignments — so the TTL backstop applies again.
        _controller.PendingDispatches = (Uri _, out Guid nodeId) =>
        {
            nodeId = Guid.Empty;
            return false;
        };

        await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);

        var retried = await evaluateAsync(_node1);
        startedAgents(retried).Length.ShouldBe(FakeAgentFamily.Names.Length);
    }
}
