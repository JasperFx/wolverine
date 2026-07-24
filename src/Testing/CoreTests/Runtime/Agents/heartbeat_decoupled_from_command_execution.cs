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
/// Regression coverage for the D1 trigger of the projection-agent-assignment churn livelock (GH-3604).
/// The node heartbeat used to be written only as the first step of <see cref="NodeAgentController.DoHealthChecksAsync"/>,
/// which ran serially in the same loop as the agent-command drain. A leader that spent ~60s burning remote
/// InvokeAsync&lt;AgentsStarted&gt; reply timeouts while starting thousands of subscription agents therefore
/// delayed its OWN next heartbeat past StaleNodeTimeout — looking dead to its peers precisely when it was
/// doing the most work, getting ejected, and resurrecting under a fresh node number. The heartbeat is now
/// written on an independent loop via <see cref="NodeAgentController.WriteHeartbeatAsync"/>, which must NOT
/// share the <c>_healthCheckGuard</c> or it would inherit the very starvation this decoupling removes.
/// </summary>
public class heartbeat_decoupled_from_command_execution
{
    private readonly WolverineOptions _options;
    private readonly INodeAgentPersistence _persistence;
    private readonly IWolverineRuntime _runtime;
    private readonly NodeAgentController _controller;

    public heartbeat_decoupled_from_command_execution()
    {
        _options = new WolverineOptions
        {
            ApplicationAssembly = GetType().Assembly
        };
        _options.Transports.NodeControlEndpoint = new FakeEndpoint("fake://self".ToUri(), EndpointRole.System);
        _options.Durability.DurabilityAgentEnabled = false; // skip MessageStoreCollection wiring
        _options.Durability.StaleNodeTimeout = 30.Seconds();

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

    [Fact]
    public async Task write_heartbeat_marks_the_health_check_for_this_node()
    {
        await _controller.WriteHeartbeatAsync();

        await _persistence.Received(1).MarkHealthCheckAsync(
            Arg.Is<WolverineNode>(n => n.NodeId == _options.UniqueNodeId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task heartbeat_is_written_even_while_a_health_check_evaluation_is_wedged()
    {
        // Park a DoHealthChecksAsync deep inside its guarded section, holding _healthCheckGuard and standing
        // in for a leader wedged draining agent commands. If WriteHeartbeatAsync shared that guard (the old
        // coupling), every heartbeat below would bounce off it and never write — which is exactly how a busy
        // leader got itself ejected.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _persistence.HasLeadershipLock().Returns(false);
        _persistence.TryAttainLeadershipLockAsync(Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                entered.TrySetResult();
                await release.Task;
                return true;
            });
        _persistence.LoadNodeAgentStateAsync(Arg.Any<CancellationToken>())
            .Returns(new NodeAgentState([SelfRow(DateTimeOffset.UtcNow)], new AgentRestrictions()));

        var wedged = _controller.DoHealthChecksAsync();
        await entered.Task.WaitAsync(5.Seconds());

        // The wedged evaluation is already past its own MarkHealthCheckAsync and is now stuck holding the
        // guard. Clear those calls, then prove the independent heartbeat path keeps writing regardless.
        _persistence.ClearReceivedCalls();

        await _controller.WriteHeartbeatAsync();
        await _controller.WriteHeartbeatAsync();
        await _controller.WriteHeartbeatAsync();

        await _persistence.Received(3).MarkHealthCheckAsync(
            Arg.Any<WolverineNode>(), Arg.Any<CancellationToken>());

        release.SetResult();
        await wedged.WaitAsync(5.Seconds());
    }

    [Fact]
    public async Task write_heartbeat_is_a_no_op_once_cancelled()
    {
        var cancellation = new CancellationTokenSource();
        var controller = new NodeAgentController(
            _runtime,
            _persistence,
            Array.Empty<IAgentFamily>(),
            NullLogger<NodeAgentController>.Instance,
            cancellation.Token);

        await cancellation.CancelAsync();

        await controller.WriteHeartbeatAsync();

        await _persistence.DidNotReceive().MarkHealthCheckAsync(
            Arg.Any<WolverineNode>(), Arg.Any<CancellationToken>());
    }
}
