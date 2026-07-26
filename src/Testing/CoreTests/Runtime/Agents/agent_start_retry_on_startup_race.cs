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
/// Regression coverage for GH-3519. On a multi-store Marten host, one event-subscription agent — a
/// different one on every boot — failed its very first assignment start because it was evaluated before
/// its store's high-water detection was up, and then sat wedged for the life of the process. The daemon
/// side is fixed in JasperFx.Events 2.36.x: the start failure now arrives as a <c>ShardStartException</c>
/// that names its cause (jasperfx#534) and the half-started shard is released rather than orphaned
/// (jasperfx#540), so the next attempt succeeds. What was left on this side was WHEN that next attempt
/// happens — the node retried only on the next assignment reevaluation, so the loser of a sub-second
/// startup race idled for a full CheckAssignmentPeriod (30s by default) while its high-water climbed.
/// </summary>
public class agent_start_retry_on_startup_race
{
    private readonly WolverineOptions _options;
    private readonly IWolverineRuntime _runtime;
    private readonly CancellationTokenSource _cancellation = new();

    public agent_start_retry_on_startup_race()
    {
        _options = new WolverineOptions { ApplicationAssembly = GetType().Assembly };
        _options.Durability.Mode = DurabilityMode.Solo;
        _options.Durability.DurabilityAgentEnabled = false;
        _options.Durability.CheckAssignmentPeriod = 1.Hours();

        // Keep the test fast; the retry COUNT is what's under test, not the pacing.
        _options.Durability.AgentStartRetryDelay = 1.Milliseconds();

        _runtime = Substitute.For<IWolverineRuntime>();
        _runtime.Options.Returns(_options);
        _runtime.DurabilitySettings.Returns(_options.Durability);
        _runtime.Observer.Returns(Substitute.For<IWolverineObserver>());
    }

    private NodeAgentController controllerFor(params FlakyAgent[] agents)
    {
        var family = new FlakyAgentFamily("event-subscriptions");
        foreach (var agent in agents)
        {
            family.Add(agent);
        }

        return new NodeAgentController(_runtime, Substitute.For<INodeAgentPersistence>(), [family],
            NullLogger<NodeAgentController>.Instance, _cancellation.Token);
    }

    [Fact]
    public async Task recovers_from_a_start_that_loses_the_first_assignment_race()
    {
        var uri = new Uri("event-subscriptions://marten/iincidentsstore/localhost.postgres/incident/all");

        // Exactly the reported shape: high-water detection isn't up yet on the first attempt and is by
        // the second.
        var agent = new FlakyAgent(uri, failuresBeforeSuccess: 1);
        var controller = controllerFor(agent);

        await controller.StartAgentAsync(uri);

        agent.AttemptCount.ShouldBe(2);
        agent.Status.ShouldBe(AgentStatus.Running);
        controller.Agents.ContainsKey(uri).ShouldBeTrue();
    }

    [Fact]
    public async Task gives_up_after_the_configured_attempts_and_preserves_the_daemon_s_reason()
    {
        var uri = new Uri("event-subscriptions://marten/incident/all");
        var agent = new FlakyAgent(uri, failuresBeforeSuccess: int.MaxValue);
        var controller = controllerFor(agent);

        var ex = await Should.ThrowAsync<AgentStartingException>(() => controller.StartAgentAsync(uri));

        // Default is 2 retries on top of the initial attempt. A failure that outlives them is left to
        // the next assignment reevaluation rather than retried harder here.
        agent.AttemptCount.ShouldBe(3);

        // The daemon's reason has to survive the wrapping, or we are back to the causeless "Unable to
        // start a subscription agent" that made this issue undiagnosable in the first place.
        ex.InnerException.ShouldNotBeNull();
        ex.InnerException.Message.ShouldContain("Incident:All");
        ex.InnerException.Message.ShouldContain("High-water detection is not running yet");

        controller.Agents.ContainsKey(uri).ShouldBeFalse();
    }

    [Fact]
    public async Task retries_can_be_turned_off()
    {
        var uri = new Uri("event-subscriptions://marten/incident/all");
        _options.Durability.AgentStartRetryAttempts = 0;

        var agent = new FlakyAgent(uri, failuresBeforeSuccess: 1);
        var controller = controllerFor(agent);

        await Should.ThrowAsync<AgentStartingException>(() => controller.StartAgentAsync(uri));

        agent.AttemptCount.ShouldBe(1);
    }

    [Fact]
    public async Task a_healthy_agent_still_starts_on_the_first_attempt()
    {
        var uri = new Uri("event-subscriptions://marten/incident/all");
        var agent = new FlakyAgent(uri, failuresBeforeSuccess: 0);
        var controller = controllerFor(agent);

        await controller.StartAgentAsync(uri);

        agent.AttemptCount.ShouldBe(1);
    }

    private class FlakyAgentFamily : IAgentFamily
    {
        private readonly Dictionary<Uri, FlakyAgent> _agents = new();

        public FlakyAgentFamily(string scheme) => Scheme = scheme;

        public void Add(FlakyAgent agent) => _agents[agent.Uri] = agent;

        public string Scheme { get; }

        public ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
            => ValueTask.FromResult<IReadOnlyList<Uri>>(_agents.Keys.ToList());

        public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
            => ValueTask.FromResult<IAgent>(_agents[uri]);

        public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
            => ValueTask.FromResult<IReadOnlyList<Uri>>(_agents.Keys.ToList());

        public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments) => ValueTask.CompletedTask;
    }

    private class FlakyAgent : IAgent
    {
        private readonly int _failuresBeforeSuccess;

        public FlakyAgent(Uri uri, int failuresBeforeSuccess)
        {
            Uri = uri;
            _failuresBeforeSuccess = failuresBeforeSuccess;
        }

        public int AttemptCount { get; private set; }

        public Uri Uri { get; }
        public AgentStatus Status { get; private set; } = AgentStatus.Stopped;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            AttemptCount++;
            if (AttemptCount <= _failuresBeforeSuccess)
            {
                // Stands in for the ShardStartException JasperFxAsyncDaemon.StartAgentAsync(ShardName)
                // now throws instead of a bare, causeless Exception (its constructors are internal to
                // JasperFx.Events, so this reproduces the message shape rather than the type).
                throw new Exception(
                    "Unable to start a subscription agent for 'Incident:All'. High-water detection is not running yet, so the shard could not be positioned.");
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
