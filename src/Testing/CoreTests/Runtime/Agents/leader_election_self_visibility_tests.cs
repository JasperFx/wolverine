using CoreTests.Transports;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// Regression coverage for GH-2682. Stale snapshot reads (read replica lag,
/// snapshot isolation, GC pause between the heartbeat write and the read,
/// Oracle session-TZ round-trip, an aggressive <c>StaleNodeTimeout</c>) used
/// to fold the current node into the staleNodes filter inside
/// <see cref="NodeAgentController.DoHealthChecksAsync"/>. The tick then crashed
/// in <c>tryStartLeadershipAsync</c> with NRE on
/// <c>self!.AssignAgents([LeaderUri])</c> — *after* IsLeader had been set
/// true, the leadership lock was held, AssumedLeadership had fired, and the
/// assignment row was written. The cluster ended up half-elected with no
/// agent dispatch. The fix is "we know we're alive because we just wrote our
/// own heartbeat" — never let self into staleNodes, and inject self if the
/// snapshot didn't include it at all.
/// </summary>
public class leader_election_self_visibility_tests
{
    private readonly WolverineOptions _options;
    private readonly INodeAgentPersistence _persistence;
    private readonly IWolverineRuntime _runtime;
    private readonly NodeAgentController _controller;

    public leader_election_self_visibility_tests()
    {
        _options = new WolverineOptions
        {
            ApplicationAssembly = GetType().Assembly
        };
        _options.Transports.NodeControlEndpoint = new FakeEndpoint("fake://self".ToUri(), EndpointRole.System);
        _options.Durability.DurabilityAgentEnabled = false; // skip MessageStoreCollection wiring
        _options.Durability.StaleNodeTimeout = 30.Seconds(); // matches the bug report

        _runtime = Substitute.For<IWolverineRuntime>();
        _runtime.Options.Returns(_options);
        _runtime.DurabilitySettings.Returns(_options.Durability);
        _runtime.Observer.Returns(Substitute.For<IWolverineObserver>());

        _persistence = Substitute.For<INodeAgentPersistence>();

        _controller = new NodeAgentController(
            _runtime,
            _persistence,
            Array.Empty<IAgentFamily>(),
            NullLogger<NodeAgentController>.Instance,
            CancellationToken.None);
    }

    private WolverineNode SelfRow(DateTimeOffset lastHealthCheck) => new()
    {
        NodeId = _options.UniqueNodeId,
        AssignedNodeNumber = _options.Durability.AssignedNodeNumber,
        ControlUri = _options.Transports.NodeControlEndpoint!.Uri,
        LastHealthCheck = lastHealthCheck
    };

    private WolverineNode OtherRow(DateTimeOffset lastHealthCheck) => new()
    {
        NodeId = Guid.NewGuid(),
        AssignedNodeNumber = 99,
        ControlUri = new Uri("fake://other"),
        LastHealthCheck = lastHealthCheck
    };

    private void primeForElection()
    {
        // The fix focuses on the staleness filter. Drive the controller down
        // the tryStartLeadershipAsync branch by handing it the leadership lock.
        _persistence.HasLeadershipLock().Returns(false);
        _persistence.TryAttainLeadershipLockAsync(Arg.Any<CancellationToken>())
            .Returns(true);
    }

    [Fact]
    public async Task does_not_throw_when_self_appears_stale_in_loaded_snapshot()
    {
        primeForElection();

        // Self's row in the snapshot is past the StaleNodeTimeout (30s) — the
        // exact pathology described in GH-2682. Pre-fix this folded self into
        // staleNodes and led to NRE in tryStartLeadershipAsync.
        var staleSelf = SelfRow(DateTimeOffset.UtcNow.Subtract(60.Seconds()));
        var liveOther = OtherRow(DateTimeOffset.UtcNow);

        _persistence.LoadNodeAgentStateAsync(Arg.Any<CancellationToken>())
            .Returns(new NodeAgentState(new[] { staleSelf, liveOther }, new AgentRestrictions()));

        await _controller.DoHealthChecksAsync();

        _controller.IsLeader.ShouldBeTrue();
        _controller.LastAssignments.ShouldNotBeNull();
        _controller.LastAssignments!.Nodes
            .ShouldContain(n => n.NodeId == _options.UniqueNodeId,
                "self must remain in the assignment grid even when the snapshot reported it as stale");
    }

    [Fact]
    public async Task does_not_eject_self_even_when_snapshot_marks_self_stale()
    {
        primeForElection();

        var staleSelf = SelfRow(DateTimeOffset.UtcNow.Subtract(60.Seconds()));
        _persistence.LoadNodeAgentStateAsync(Arg.Any<CancellationToken>())
            .Returns(new NodeAgentState(new[] { staleSelf }, new AgentRestrictions()));

        await _controller.DoHealthChecksAsync();

        // ejectStaleNodes used to skip self by AssignedNodeNumber match (line
        // 179 of NodeAgentController.HeartBeat.cs); the in-memory list still
        // dropped self though. With the fix self must never even reach the
        // staleNodes array, so DeleteAsync must not be called for self.
        await _persistence.DidNotReceive()
            .DeleteAsync(_options.UniqueNodeId, _options.Durability.AssignedNodeNumber);
    }

    [Fact]
    public async Task injects_self_when_snapshot_omits_self_entirely()
    {
        primeForElection();

        // Snapshot has no row for self at all (read-after-write lag against the
        // upsert above, brand-new node still propagating). The defensive
        // self-injection branch in DoHealthChecksAsync must keep the election
        // path safe.
        var liveOther = OtherRow(DateTimeOffset.UtcNow);
        _persistence.LoadNodeAgentStateAsync(Arg.Any<CancellationToken>())
            .Returns(new NodeAgentState(new[] { liveOther }, new AgentRestrictions()));

        await _controller.DoHealthChecksAsync();

        _controller.IsLeader.ShouldBeTrue();
        _controller.LastAssignments.ShouldNotBeNull();
        _controller.LastAssignments!.Nodes
            .ShouldContain(n => n.NodeId == _options.UniqueNodeId,
                "self must be injected into the assignment grid when the snapshot omits it");
    }

    [Fact]
    public async Task evaluate_assignments_injects_self_when_caller_passes_non_empty_list_without_self()
    {
        // Defense in depth for EvaluateAssignmentsAsync. The pre-existing guard
        // at the top of the method only fired when the list was empty; a stale
        // snapshot that filtered self out but kept other nodes slipped through.
        var liveOther = OtherRow(DateTimeOffset.UtcNow);

        var commands = await _controller.EvaluateAssignmentsAsync(
            new[] { liveOther },
            new AgentRestrictions());

        commands.ShouldNotBeNull();
        _controller.LastAssignments.ShouldNotBeNull();
        _controller.LastAssignments!.Nodes
            .ShouldContain(n => n.NodeId == _options.UniqueNodeId);
    }
}
