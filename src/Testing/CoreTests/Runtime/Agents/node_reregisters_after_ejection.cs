using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// Regression coverage for GH-3604 / D2. When a peer deletes a still-live node's row out from under it
/// (an ejection under churn), the node's next persistence touch must re-register with its REAL identity --
/// the same AssignedNodeNumber, the capabilities captured at StartLocalAgentProcessingAsync, and its agent
/// assignments restored -- instead of letting the store blindly insert a capability-less skeleton with a
/// fresh node number. An empty-capability row is a candidate for nothing in capability-matched
/// distribution, so the blind skeleton silently shrank the cluster and the grid re-issued its whole agent
/// universe every cycle forever. This drives the controller against a stubbed INodeAgentPersistence.
/// </summary>
public class node_reregisters_after_ejection
{
    private const int AssignedNumber = 7;

    private readonly WolverineOptions _options;
    private readonly IWolverineRuntime _runtime;
    private readonly INodeAgentPersistence _persistence = Substitute.For<INodeAgentPersistence>();
    private readonly Uri _capabilityAndAgent = new("test-family://alpha");
    private readonly CancellationTokenSource _cancellation = new();

    public node_reregisters_after_ejection()
    {
        _options = new WolverineOptions
        {
            ApplicationAssembly = GetType().Assembly
        };
        _options.Durability.Mode = DurabilityMode.Solo;
        _options.Durability.DurabilityAgentEnabled = false;
        _options.Durability.CheckAssignmentPeriod = 1.Hours();

        _runtime = Substitute.For<IWolverineRuntime>();
        _runtime.Options.Returns(_options);
        _runtime.DurabilitySettings.Returns(_options.Durability);
        _runtime.Observer.Returns(Substitute.For<IWolverineObserver>());

        // StartLocalAgentProcessingAsync captures the node number that PersistAsync hands back.
        _persistence.PersistAsync(Arg.Any<WolverineNode>(), Arg.Any<CancellationToken>())
            .Returns(AssignedNumber);
    }

    private async Task<NodeAgentController> startedControllerAsync()
    {
        var family = new FakeStaticAgentFamily("test-family", _capabilityAndAgent);

        var controller = new NodeAgentController(
            _runtime,
            _persistence,
            [family],
            NullLogger<NodeAgentController>.Instance,
            _cancellation.Token);

        // Populates _capabilities (from the family's SupportedAgentsAsync) and the assigned node number.
        await controller.StartLocalAgentProcessingAsync(_options);
        _options.Durability.AssignedNodeNumber.ShouldBe(AssignedNumber);

        return controller;
    }

    [Fact]
    public async Task re_registers_with_real_identity_when_the_row_was_deleted_out_from_under_it()
    {
        var controller = await startedControllerAsync();

        // The heartbeat/assignment persistence reports the row is gone (peer ejection).
        _persistence.MarkHealthCheckAsync(Arg.Any<WolverineNode>(), Arg.Any<CancellationToken>())
            .Returns(false);

        await controller.StartAgentAsync(_capabilityAndAgent);

        // Re-registered with the SAME node number and the captured capabilities -- never a skeleton.
        await _persistence.Received(1).ReregisterNodeAsync(
            Arg.Is<WolverineNode>(n =>
                n.NodeId == _options.UniqueNodeId
                && n.AssignedNodeNumber == AssignedNumber
                && n.Capabilities.Contains(_capabilityAndAgent)),
            Arg.Any<CancellationToken>());

        // ...and the node's running agents' assignment rows are restored.
        await _persistence.Received(1).AssignAgentsAsync(
            _options.UniqueNodeId,
            Arg.Is<IReadOnlyList<Uri>>(list => list.Contains(_capabilityAndAgent)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task does_not_re_register_when_the_row_is_still_present()
    {
        var controller = await startedControllerAsync();

        // The normal case: the row exists, heartbeat updates it.
        _persistence.MarkHealthCheckAsync(Arg.Any<WolverineNode>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await controller.StartAgentAsync(_capabilityAndAgent);

        await _persistence.DidNotReceive().ReregisterNodeAsync(
            Arg.Any<WolverineNode>(), Arg.Any<CancellationToken>());

        // The plain single-agent assignment still happens through AddAssignmentAsync, not the bulk restore.
        await _persistence.Received(1).AddAssignmentAsync(
            _options.UniqueNodeId, _capabilityAndAgent, Arg.Any<CancellationToken>());
        await _persistence.DidNotReceive().AssignAgentsAsync(
            Arg.Any<Guid>(), Arg.Any<IReadOnlyList<Uri>>(), Arg.Any<CancellationToken>());
    }

    private class FakeStaticAgentFamily : IStaticAgentFamily
    {
        private readonly FakeAgent _agent;

        public FakeStaticAgentFamily(string scheme, Uri agentUri)
        {
            Scheme = scheme;
            _agent = new FakeAgent(agentUri);
        }

        public string Scheme { get; }

        public ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
            => ValueTask.FromResult<IReadOnlyList<Uri>>([_agent.Uri]);

        public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
            => ValueTask.FromResult<IAgent>(_agent);

        public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
            => ValueTask.FromResult<IReadOnlyList<Uri>>([_agent.Uri]);

        public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments) => ValueTask.CompletedTask;
    }

    private class FakeAgent : IAgent
    {
        public FakeAgent(Uri uri) => Uri = uri;

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
    }
}
