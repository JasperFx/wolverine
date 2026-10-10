using CoreTests.Transports;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// Regression coverage for WO-6 (GH-3604) — ejection hysteresis + leader protection. A single stale
/// snapshot read (replica lag, a GC pause, an aggressive StaleNodeTimeout, or a leader momentarily busy
/// before D1 was fixed) used to be enough for a peer to DeleteAsync a very-much-alive node — wiping its
/// row, its in-flight envelope ownership, and its agent assignments — which fed the eject/resurrect
/// flapping. The destructive delete now requires N consecutive stale observations, and a follower may
/// never delete the current leader's row: only a node that actually holds the leadership lock cleans up a
/// stale leader. Routing to a stale node still stops immediately (grid exclusion, unchanged).
/// </summary>
public class ejection_hysteresis_tests
{
    private readonly WolverineOptions _options;
    private readonly INodeAgentPersistence _persistence;
    private readonly IWolverineRuntime _runtime;
    private readonly NodeAgentController _controller;
    private readonly Guid _peerId = Guid.NewGuid();

    public ejection_hysteresis_tests()
    {
        _options = new WolverineOptions { ApplicationAssembly = GetType().Assembly };
        _options.Transports.NodeControlEndpoint = new FakeEndpoint("fake://self".ToUri(), EndpointRole.System);
        _options.Durability.DurabilityAgentEnabled = false;
        _options.Durability.StaleNodeTimeout = 30.Seconds();

        _runtime = Substitute.For<IWolverineRuntime>();
        _runtime.Options.Returns(_options);
        _runtime.DurabilitySettings.Returns(_options.Durability);
        _runtime.Observer.Returns(Substitute.For<IWolverineObserver>());

        _persistence = Substitute.For<INodeAgentPersistence>();
        // Keep this node a follower; the ejection path we exercise runs before the leadership attempt.
        _persistence.TryAttainLeadershipLockAsync(Arg.Any<CancellationToken>()).Returns(false);

        _controller = new NodeAgentController(
            _runtime, _persistence, Array.Empty<IAgentFamily>(),
            NullLogger<NodeAgentController>.Instance, CancellationToken.None);
    }

    private WolverineNode Self() => new()
    {
        NodeId = _options.UniqueNodeId,
        AssignedNodeNumber = 1,
        ControlUri = _options.Transports.NodeControlEndpoint!.Uri,
        LastHealthCheck = DateTimeOffset.UtcNow
    };

    private WolverineNode Peer(bool stale, bool isLeader = false)
    {
        var node = new WolverineNode
        {
            NodeId = _peerId,
            AssignedNodeNumber = 99,
            ControlUri = new Uri("fake://peer"),
            LastHealthCheck = stale
                ? DateTimeOffset.UtcNow.Subtract(60.Seconds())
                : DateTimeOffset.UtcNow
        };
        if (isLeader) node.AssignAgents([NodeAgentController.LeaderUri]);
        return node;
    }

    private void snapshotIs(params WolverineNode[] nodes)
        => _persistence.LoadNodeAgentStateAsync(Arg.Any<CancellationToken>())
            .Returns(new NodeAgentState(nodes, new AgentRestrictions()));

    private Task tickAsync() => _controller.DoHealthChecksAsync();

    private Task assertPeerDeleted(int times)
        => _persistence.Received(times).DeleteAsync(_peerId, 99);

    [Fact]
    public async Task does_not_eject_a_stale_peer_on_the_first_observation()
    {
        snapshotIs(Self(), Peer(stale: true));

        await tickAsync();

        await _persistence.DidNotReceive().DeleteAsync(_peerId, Arg.Any<int>());
    }

    [Fact]
    public async Task ejects_a_stale_peer_after_two_consecutive_observations()
    {
        snapshotIs(Self(), Peer(stale: true));

        await tickAsync(); // observation 1 — below threshold
        await _persistence.DidNotReceive().DeleteAsync(_peerId, Arg.Any<int>());

        await tickAsync(); // observation 2 — threshold reached
        await assertPeerDeleted(1);
    }

    [Fact]
    public async Task resets_the_streak_when_the_peer_recovers_between_observations()
    {
        snapshotIs(Self(), Peer(stale: true));
        await tickAsync(); // observation 1

        snapshotIs(Self(), Peer(stale: false));
        await tickAsync(); // peer healthy again — streak resets

        snapshotIs(Self(), Peer(stale: true));
        await tickAsync(); // stale again, but only the first of a NEW streak

        await _persistence.DidNotReceive().DeleteAsync(_peerId, Arg.Any<int>());
    }

    [Fact]
    public async Task a_follower_never_deletes_a_stale_leaders_row()
    {
        // Isolate leader protection from hysteresis: one observation is enough to reach the threshold.
        _options.Durability.StaleNodeEjectionThreshold = 1;
        _persistence.HasLeadershipLock().Returns(false); // this node is a follower

        snapshotIs(Self(), Peer(stale: true, isLeader: true));

        await tickAsync();
        await tickAsync();

        // The freed advisory lock lets a follower attain leadership through the normal path; it must NOT
        // destroy the leader's row itself.
        await _persistence.DidNotReceive().DeleteAsync(_peerId, Arg.Any<int>());
    }

    [Fact]
    public async Task the_leadership_lock_holder_may_eject_a_stale_leader()
    {
        _options.Durability.StaleNodeEjectionThreshold = 1;
        _persistence.HasLeadershipLock().Returns(true); // this node has taken over leadership

        snapshotIs(Self(), Peer(stale: true, isLeader: true));

        await tickAsync();

        await assertPeerDeleted(1);
    }

    /// <summary>
    /// GH-3698. The observer writes one DormantNodeEjected record per node it is handed, and it used to be
    /// handed the whole stale list — including every node the checks above deliberately spare. A node that
    /// blipped stale for a single tick and recovered was therefore recorded as ejected while still
    /// heartbeating, which is exactly the contradiction reported against the production cluster: an
    /// "ejected" record sitting next to a fresh health_check.
    /// </summary>
    [Fact]
    public async Task records_no_ejection_for_a_peer_still_inside_the_hysteresis_window()
    {
        snapshotIs(Self(), Peer(stale: true));

        await tickAsync(); // observed stale, but below the ejection threshold

        await _persistence.DidNotReceive().DeleteAsync(_peerId, Arg.Any<int>());
        await _runtime.Observer.DidNotReceive().StaleNodes(Arg.Any<IReadOnlyList<WolverineNode>>());
    }

    [Fact]
    public async Task records_no_ejection_for_a_stale_leader_a_follower_may_not_delete()
    {
        _options.Durability.StaleNodeEjectionThreshold = 1;
        _persistence.HasLeadershipLock().Returns(false);

        snapshotIs(Self(), Peer(stale: true, isLeader: true));

        await tickAsync();

        await _persistence.DidNotReceive().DeleteAsync(_peerId, Arg.Any<int>());
        await _runtime.Observer.DidNotReceive().StaleNodes(Arg.Any<IReadOnlyList<WolverineNode>>());
    }

    [Fact]
    public async Task records_an_ejection_for_the_peer_it_actually_deleted()
    {
        _options.Durability.StaleNodeEjectionThreshold = 1;

        snapshotIs(Self(), Peer(stale: true));

        await tickAsync();

        await assertPeerDeleted(1);
        await _runtime.Observer.Received(1)
            .StaleNodes(Arg.Is<IReadOnlyList<WolverineNode>>(x => x.Count == 1 && x[0].NodeId == _peerId));
    }

    /// <summary>
    /// PR #4537's finding. A departed node's lane in the command dispatcher otherwise waits out a reply window
    /// nobody will send, holding the agents it was moving in the leader's ledger for up to twenty-five minutes.
    /// The controller reports a departure at the moment it ejects a stale row, and never inside the hysteresis
    /// window -- a node that blips stale for a tick is not gone.
    /// </summary>
    [Fact]
    public async Task reports_the_departure_of_a_peer_when_it_ejects_it_and_not_before()
    {
        var departed = new List<Guid>();
        _controller.NodeDeparted = departed.Add;

        snapshotIs(Self(), Peer(stale: true));

        await tickAsync(); // stale observation 1 of 2
        departed.ShouldBeEmpty();

        await tickAsync(); // ejected
        departed.ShouldBe([_peerId]);
    }

    [Fact]
    public async Task reports_the_departure_of_a_peer_whose_row_is_simply_gone()
    {
        var departed = new List<Guid>();
        _controller.NodeDeparted = departed.Add;

        snapshotIs(Self(), Peer(stale: false));
        await tickAsync();
        departed.ShouldBeEmpty();

        // A clean shutdown deletes the node's own row; a peer that ejected it does the same
        snapshotIs(Self());
        await tickAsync();
        departed.ShouldBe([_peerId]);

        // Said once, not on every later tick
        await tickAsync();
        departed.Count.ShouldBe(1);
    }

    /// <summary>
    /// GH-4897 exempted a STALE node's agents from the takeover hold, because they are running nowhere. A node
    /// that shut down cleanly deleted its own row, and its agents are orphans in exactly the same way -- but
    /// they were held for an evaluation on every leader change of every rolling deploy.
    /// </summary>
    [Fact]
    public async Task the_agents_of_a_node_whose_row_vanished_are_orphans_for_the_takeover_hold()
    {
        var running = new Uri("fake://one");
        var peer = Peer(stale: false);
        peer.AssignAgents([running]);

        snapshotIs(Self(), peer);
        await tickAsync();
        _controller.OrphanedByStaleNodes.ShouldBeEmpty();

        snapshotIs(Self());
        await tickAsync();
        _controller.OrphanedByStaleNodes.ShouldBe([running]);
    }
}
