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
///     GH-5760. The leadership compliance specs make a leader "go stale" with DisableAgentsAsync, which releases the
///     leadership lock. It did that without waiting for a health check tick that was already running on the same node,
///     and that tick renews the lock. On a lease store (RavenDB, CosmosDB) a renewal still in flight when the release
///     arrives keeps the lock: the release is refused as stale, or the renewal lands after it and takes the lock again,
///     either way for the full five minute lease. The successor then could not take the leadership, and
///     take_over_leader_ship_if_leader_becomes_stale failed with "node 2 did not assume the leadership".
/// </summary>
public class disabling_agents_waits_for_an_in_flight_health_check_5760
{
    private readonly WolverineOptions _options;
    private readonly INodeAgentPersistence _persistence;
    private readonly NodeAgentController _controller;

    // The lease as the store sees it
    private bool _lockIsHeld;

    private readonly TaskCompletionSource _renewalStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _renewalLands = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public disabling_agents_waits_for_an_in_flight_health_check_5760()
    {
        _options = new WolverineOptions
        {
            ApplicationAssembly = GetType().Assembly
        };
        _options.Transports.NodeControlEndpoint = new FakeEndpoint("fake://self".ToUri(), EndpointRole.System);
        _options.Durability.DurabilityAgentEnabled = false;
        _options.Durability.AssignedNodeNumber = 1;

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(_options);
        runtime.DurabilitySettings.Returns(_options.Durability);
        runtime.Observer.Returns(Substitute.For<IWolverineObserver>());

        _persistence = Substitute.For<INodeAgentPersistence>();

        var self = new WolverineNode
        {
            NodeId = _options.UniqueNodeId,
            AssignedNodeNumber = 1,
            ControlUri = _options.Transports.NodeControlEndpoint!.Uri,
            LastHealthCheck = DateTimeOffset.UtcNow
        };
        _persistence.LoadNodeAgentStateAsync(Arg.Any<CancellationToken>())
            .Returns(new NodeAgentState([self], new AgentRestrictions()));

        _persistence.HasLeadershipLockAsync(Arg.Any<CancellationToken>()).Returns(_ => _lockIsHeld);

        // The renewal is sent, and lands at the store whenever the test lets it
        _persistence.TryAttainLeadershipLockAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            _renewalStarted.TrySetResult();
            await _renewalLands.Task;
            _lockIsHeld = true;
            return true;
        });

        _persistence.ReleaseLeadershipLockAsync().Returns(_ =>
        {
            _lockIsHeld = false;
            return Task.CompletedTask;
        });

        _controller = new NodeAgentController(
            runtime,
            _persistence,
            Array.Empty<IAgentFamily>(),
            NullLogger<NodeAgentController>.Instance,
            CancellationToken.None);
    }

    [Fact]
    public async Task the_lock_is_released_after_a_renewal_that_was_already_in_flight()
    {
        var tick = _controller.DoHealthChecksAsync();
        await _renewalStarted.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        var disabling = _controller.DisableAgentsAsync();

        _renewalLands.SetResult();
        await tick.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        await disabling.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        _lockIsHeld.ShouldBeFalse(
            "a disabled node must not keep the leadership lock, or no other node can take over until its lease expires");
    }

    [Fact]
    public async Task no_health_check_runs_once_the_agents_are_disabled()
    {
        _renewalLands.SetResult();
        await _controller.DisableAgentsAsync();

        await _controller.DoHealthChecksAsync();

        await _persistence.DidNotReceive().TryAttainLeadershipLockAsync(Arg.Any<CancellationToken>());
        _lockIsHeld.ShouldBeFalse();
    }
}
