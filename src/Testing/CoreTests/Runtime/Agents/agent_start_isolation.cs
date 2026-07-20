using JasperFx;
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
/// Regression coverage for GH-3519. When a single agent cannot start during a Solo-mode
/// assignment reevaluation (e.g. an event-subscription shard that loses a first-assignment
/// startup race with high-water detection), that one failure must not skip the remaining,
/// healthy sibling agents in the same family. Before the fix the family loop bailed on the
/// first throw, so every agent after the wedged one stayed unstarted — reproduced in the wild
/// as "two of three projection shards dead on that boot; only one ran."
/// </summary>
public class agent_start_isolation
{
    private readonly WolverineOptions _options;
    private readonly IWolverineRuntime _runtime;
    private readonly CancellationTokenSource _cancellation = new();

    public agent_start_isolation()
    {
        _options = new WolverineOptions
        {
            ApplicationAssembly = GetType().Assembly
        };
        _options.Durability.Mode = DurabilityMode.Solo;
        _options.Durability.DurabilityAgentEnabled = false; // skip MessageStoreCollection wiring
        // Keep the recurring loop out of the way; the assertion is on the single startup pass.
        _options.Durability.CheckAssignmentPeriod = 1.Hours();

        _runtime = Substitute.For<IWolverineRuntime>();
        _runtime.Options.Returns(_options);
        _runtime.DurabilitySettings.Returns(_options.Durability);
        _runtime.Observer.Returns(Substitute.For<IWolverineObserver>());
    }

    [Fact]
    public async Task one_failing_agent_does_not_prevent_its_siblings_from_starting()
    {
        var healthyBefore = new Uri("test-family://a");
        var wedged = new Uri("test-family://b");
        var healthyAfter = new Uri("test-family://c");

        var family = new FakeAgentFamily("test-family");
        family.Add(new FakeAgent(healthyBefore));
        family.Add(new FakeAgent(wedged) { ThrowOnStart = true });
        family.Add(new FakeAgent(healthyAfter));

        var controller = new NodeAgentController(
            _runtime,
            Substitute.For<INodeAgentPersistence>(),
            [family],
            NullLogger<NodeAgentController>.Instance,
            _cancellation.Token);

        await controller.StartSoloModeAsync();
        await _cancellation.CancelAsync();

        // The agent listed before the wedged one starts (it always did), but critically the
        // sibling listed AFTER the wedged one must also be started now.
        controller.Agents.ContainsKey(healthyBefore).ShouldBeTrue();
        controller.Agents.ContainsKey(healthyAfter).ShouldBeTrue(
            "a sibling agent after the failing one must still be started (GH-3519)");

        // The agent that could not start is not recorded as running, so it is retried next tick.
        controller.Agents.ContainsKey(wedged).ShouldBeFalse();
    }

    private class FakeAgentFamily : IAgentFamily
    {
        private readonly Dictionary<Uri, FakeAgent> _agents = new();

        public FakeAgentFamily(string scheme)
        {
            Scheme = scheme;
        }

        public void Add(FakeAgent agent) => _agents[agent.Uri] = agent;

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

        public bool ThrowOnStart { get; init; }

        public Uri Uri { get; }
        public AgentStatus Status { get; private set; } = AgentStatus.Stopped;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (ThrowOnStart)
            {
                throw new Exception("Unable to start a subscription agent for " + Uri);
            }

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
