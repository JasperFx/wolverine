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
/// Coverage for GH-3970: an agent this node cannot BUILD OR START is released to a capable peer, instead
/// of being requested on the same node again on every assignment tick forever.
///
/// <para>This is the gap GH-3888 could not cover. That release is driven by the stall detector sweeping
/// the agents this node is actually running (<c>NodeAgentController.Agents</c>) and reading
/// <c>LocalRestartsExhausted</c> off the live instance. When <c>IAgentFamily.BuildAgentAsync</c> throws
/// there IS no instance — nothing is ever registered — so no restart budget is ever consumed and that
/// sweep structurally cannot see the agent. The exception was caught, logged, and dropped.</para>
///
/// <para>The leader, meanwhile, learns only that the agent is "unconfirmed", which it deliberately does
/// not treat as a failure — GH-3750 fixed exactly the opposite bug, where slow starts got re-placed onto
/// other nodes. So the assignment stands and the same agent is requested on the same node again next
/// tick. The field failure: a blue/green fleet where the two sides carry disjoint projection versions,
/// each handed agents for the other's version, failing with
/// <c>ArgumentOutOfRangeException: Unable to find a shard with path '…/v19/&lt;tenant&gt;'</c>. Fleet-wide
/// projection progress was byte-identical across 54 minutes; only a pod restart ever cleared it.</para>
///
/// <para>The fix counts consecutive failed starts on the node that caught them, and feeds an exhausted
/// budget into GH-3888's existing release — the same capability embargo, so the leader cannot hand the
/// agent straight back.</para>
/// </summary>
public class failed_agent_start_release
{
    private readonly WolverineOptions _options;
    private readonly IWolverineRuntime _runtime;
    private readonly IWolverineObserver _observer = Substitute.For<IWolverineObserver>();
    private readonly INodeAgentPersistence _persistence = Substitute.For<INodeAgentPersistence>();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<WolverineNode> _reregistrations = new();

    private static readonly Uri AgentUri =
        new("event-subscriptions://marten/main/claims438/claim_lines/all/v19/01009333");

    public failed_agent_start_release()
    {
        _options = new WolverineOptions { ApplicationAssembly = GetType().Assembly };
        _options.Durability.Mode = DurabilityMode.Solo;
        _options.Durability.DurabilityAgentEnabled = false;
        _options.Durability.CheckAssignmentPeriod = 1.Hours();

        // One attempt per tick keeps the tests counting assignment ticks rather than inner retries; the
        // budget under test is the OUTER one. See MaxAgentStartFailuresBeforeRelease.
        _options.Durability.AgentStartRetryAttempts = 0;
        _options.Durability.AgentStartRetryDelay = TimeSpan.Zero;

        _runtime = Substitute.For<IWolverineRuntime>();
        _runtime.Options.Returns(_options);
        _runtime.DurabilitySettings.Returns(_options.Durability);
        _runtime.Observer.Returns(_observer);

        _persistence.MarkHealthCheckAsync(Arg.Any<WolverineNode>(), Arg.Any<CancellationToken>())
            .Returns(true);

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

    private async Task<(NodeAgentController Controller, UnbuildableAgentFamily Family)> controllerAsync()
    {
        var family = new UnbuildableAgentFamily("event-subscriptions", AgentUri);

        var controller = new NodeAgentController(_runtime, _persistence, [family],
            NullLogger<NodeAgentController>.Instance, _cancellation.Token);

        // Captures the node's advertised capabilities, so a release is observable as a capability set
        // that shrinks. The family advertises the agent even though it cannot build it — which is
        // exactly the reported shape: the leader had every reason to think this node could run it.
        await controller.StartLocalAgentProcessingAsync(_options);

        return (controller, family);
    }

    /// <summary>
    /// Drive one assignment tick's worth of work: the leader asks this node to start the agent, and the
    /// start throws. Mirrors StartAgents.StartBatchAsync, which catches and logs.
    /// </summary>
    private static async Task failOneStartAsync(NodeAgentController controller)
    {
        await Should.ThrowAsync<Exception>(() => controller.StartAgentAsync(AgentUri));
    }

    [Fact]
    public async Task a_start_that_keeps_throwing_is_released_to_a_capable_peer()
    {
        var (controller, family) = await controllerAsync();
        nodeStateHas(peer(advertisesAgent: true));

        for (var i = 0; i < _options.Durability.MaxAgentStartFailuresBeforeRelease; i++)
        {
            await failOneStartAsync(controller);
        }

        await controller.ReportFailedLocalAgentsAsync();

        // The assignment row is dropped even though nothing was ever running here, which is what lets the
        // leader place the agent somewhere else. Before the fix this row was the only thing the leader
        // had, and it never changed.
        await _persistence.Received(1)
            .RemoveAssignmentAsync(_options.UniqueNodeId, AgentUri, Arg.Any<CancellationToken>());

        // The anti-bounce half: this node no longer advertises the capability, so capability-matched
        // distribution cannot hand the agent straight back to the node that just failed it.
        _reregistrations.ShouldNotBeEmpty();
        _reregistrations.Last().Capabilities.ShouldNotContain(AgentUri);

        await _observer.Received(1).AgentReleased(AgentUri, Arg.Any<ShardFailure?>());

        family.BuildAttempts.ShouldBe(_options.Durability.MaxAgentStartFailuresBeforeRelease);
    }

    [Fact]
    public async Task the_budget_is_not_spent_early()
    {
        var (controller, _) = await controllerAsync();
        nodeStateHas(peer(advertisesAgent: true));

        // One short of the budget. A start failure is not on its own evidence that this node can never
        // run the agent — GH-3519's first-assignment race is a real, self-healing case — so the release
        // must stay patient.
        for (var i = 0; i < _options.Durability.MaxAgentStartFailuresBeforeRelease - 1; i++)
        {
            await failOneStartAsync(controller);
            await controller.ReportFailedLocalAgentsAsync();
        }

        _reregistrations.ShouldBeEmpty();
        await _observer.DidNotReceive().AgentReleased(Arg.Any<Uri>(), Arg.Any<ShardFailure?>());
    }

    [Fact]
    public async Task a_successful_start_clears_the_run_of_failures()
    {
        var (controller, family) = await controllerAsync();
        nodeStateHas(peer(advertisesAgent: true));

        await failOneStartAsync(controller);
        await failOneStartAsync(controller);

        // Whatever the start was racing has come up. The count is CONSECUTIVE failures, so this has to
        // wipe the slate — otherwise a long-lived node accumulates unrelated failures over days and
        // releases a perfectly healthy agent on one later blip.
        family.CanBuild = true;
        await controller.StartAgentAsync(AgentUri);

        // Now wedge it and fail again. Reporting Stopped while still registered sends the next start
        // through GH-3519's wedge recovery, which evicts the registration and re-drives the build — so
        // this really is a fresh failed start rather than the idempotent early return.
        family.Built.ShouldNotBeNull().Wedge();
        family.CanBuild = false;

        await failOneStartAsync(controller);
        await controller.ReportFailedLocalAgentsAsync();

        await _observer.DidNotReceive().AgentReleased(Arg.Any<Uri>(), Arg.Any<ShardFailure?>());
        _reregistrations.ShouldBeEmpty();
    }

    [Fact]
    public async Task keeps_retrying_locally_when_no_live_peer_advertises_the_capability()
    {
        var (controller, _) = await controllerAsync();

        // A live peer exists but cannot run this agent. Releasing would strand the agent entirely, so the
        // sweep declines and local retries continue — the least-bad option.
        nodeStateHas(peer(advertisesAgent: false));

        for (var i = 0; i < _options.Durability.MaxAgentStartFailuresBeforeRelease; i++)
        {
            await failOneStartAsync(controller);
        }

        await controller.ReportFailedLocalAgentsAsync();

        _reregistrations.ShouldBeEmpty();
        await _persistence.DidNotReceive()
            .RemoveAssignmentAsync(Arg.Any<Guid>(), Arg.Any<Uri>(), Arg.Any<CancellationToken>());
        await _observer.DidNotReceive().AgentReleased(Arg.Any<Uri>(), Arg.Any<ShardFailure?>());
    }

    [Fact]
    public async Task a_stale_peer_advertising_the_capability_does_not_count()
    {
        var (controller, _) = await controllerAsync();

        // The only capable peer stopped heartbeating long ago — releasing to it would freeze the agent in
        // a different place. Same outcome as having no capable peer at all.
        nodeStateHas(peer(advertisesAgent: true, heartbeatAge: 10.Minutes()));

        for (var i = 0; i < _options.Durability.MaxAgentStartFailuresBeforeRelease; i++)
        {
            await failOneStartAsync(controller);
        }

        await controller.ReportFailedLocalAgentsAsync();

        _reregistrations.ShouldBeEmpty();
        await _observer.DidNotReceive().AgentReleased(Arg.Any<Uri>(), Arg.Any<ShardFailure?>());
    }

    [Fact]
    public async Task a_declined_release_refunds_the_budget_rather_than_releasing_on_the_next_tick()
    {
        var (controller, _) = await controllerAsync();
        nodeStateHas(peer(advertisesAgent: false));

        for (var i = 0; i < _options.Durability.MaxAgentStartFailuresBeforeRelease; i++)
        {
            await failOneStartAsync(controller);
        }

        await controller.ReportFailedLocalAgentsAsync();

        // The refund means the very next failure must NOT immediately re-trigger a release attempt; a
        // full fresh budget has to be burned first. Otherwise a node with no capable peer re-evaluates
        // (and re-reads the whole node table) on every single tick forever.
        await failOneStartAsync(controller);
        await controller.ReportFailedLocalAgentsAsync();

        await _persistence.Received(1).LoadNodeAgentStateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task release_can_be_disabled_entirely()
    {
        _options.Durability.MaxAgentStartFailuresBeforeRelease = 0;

        var (controller, _) = await controllerAsync();
        nodeStateHas(peer(advertisesAgent: true));

        for (var i = 0; i < 10; i++)
        {
            await failOneStartAsync(controller);
            await controller.ReportFailedLocalAgentsAsync();
        }

        // The pre-GH-3970 behaviour, kept available for anyone who depends on it: retry here forever.
        _reregistrations.ShouldBeEmpty();
        await _observer.DidNotReceive().AgentReleased(Arg.Any<Uri>(), Arg.Any<ShardFailure?>());
    }

    [Fact]
    public async Task the_capability_is_advertised_again_after_the_release_cooldown()
    {
        var clock = new FrozenClock(DateTimeOffset.UtcNow);
        var (controller, _) = await controllerAsync();
        controller.TimeProvider = clock;
        nodeStateHas(peer(advertisesAgent: true));

        for (var i = 0; i < _options.Durability.MaxAgentStartFailuresBeforeRelease; i++)
        {
            await failOneStartAsync(controller);
        }

        await controller.ReportFailedLocalAgentsAsync();
        _reregistrations.Last().Capabilities.ShouldNotContain(AgentUri);

        await controller.RestoreExpiredReleaseEmbargoesAsync();
        _reregistrations.Last().Capabilities.ShouldNotContain(AgentUri);

        // A node whose transient fault has passed — a deployment that finally carries the right
        // projection version — becomes an ordinary candidate again rather than being written off.
        clock.Advance(_options.Durability.AgentReleaseCooldown + 1.Minutes());
        await controller.RestoreExpiredReleaseEmbargoesAsync();
        _reregistrations.Last().Capabilities.ShouldContain(AgentUri);
    }

    private sealed class FrozenClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    /// <summary>
    /// A family that ADVERTISES the agent but cannot build it — the reported blue/green shape, where a
    /// node's capability set and what it can actually construct had diverged. Throws the same exception
    /// type the field report carried.
    /// </summary>
    private class UnbuildableAgentFamily : IStaticAgentFamily
    {
        private readonly Uri _uri;

        public UnbuildableAgentFamily(string scheme, Uri uri)
        {
            Scheme = scheme;
            _uri = uri;
        }

        public string Scheme { get; }
        public bool CanBuild { get; set; }
        public int BuildAttempts { get; private set; }
        public BuiltAgent? Built { get; private set; }

        public ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
            => ValueTask.FromResult<IReadOnlyList<Uri>>([_uri]);

        public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
            => ValueTask.FromResult<IReadOnlyList<Uri>>([_uri]);

        public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
        {
            BuildAttempts++;

            if (!CanBuild)
            {
                throw new ArgumentOutOfRangeException("shardPath",
                    "Unable to find a shard with path 'claim_lines/all/v19/01009333'");
            }

            Built = new BuiltAgent(uri);
            return ValueTask.FromResult<IAgent>(Built);
        }

        public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments) => ValueTask.CompletedTask;
    }

    private class BuiltAgent : IAgent
    {
        public BuiltAgent(Uri uri) => Uri = uri;

        public Uri Uri { get; }
        public AgentStatus Status { get; private set; } = AgentStatus.Stopped;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Status = AgentStatus.Running;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Status = AgentStatus.Stopped;
            return Task.CompletedTask;
        }

        /// <summary>Report Stopped while still registered — the GH-3519 wedged-shard shape.</summary>
        public void Wedge() => Status = AgentStatus.Stopped;
    }
}
