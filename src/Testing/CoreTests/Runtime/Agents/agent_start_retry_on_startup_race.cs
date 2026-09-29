using JasperFx;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Daemon;
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
    private readonly IWolverineObserver _observer = Substitute.For<IWolverineObserver>();
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
        _runtime.Observer.Returns(_observer);
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

    // GH-4676. jasperfx#912 classified WHY a shard would not start, and two of those reasons describe a
    // standing condition rather than a race. Before this, a projection that was simply not registered
    // burned the full retry budget on every reevaluation, forever -- the GH-3519 loop for a cause no
    // retry can fix.

    private static Func<Exception> failing(ShardStartFailureReason reason)
        => () => new ShardStartException("Incident:All", "the daemon said so", reason);

    [Theory]
    [InlineData(ShardStartFailureReason.ShardNotRegistered)]
    [InlineData(ShardStartFailureReason.AgentPaused)]
    public async Task a_reason_no_retry_can_fix_stops_the_loop_at_once(ShardStartFailureReason reason)
    {
        var uri = new Uri("event-subscriptions://marten/incident/all");
        var agent = new FlakyAgent(uri, int.MaxValue, failing(reason));
        var controller = controllerFor(agent);

        await Should.ThrowAsync<AgentStartingException>(() => controller.StartAgentAsync(uri));

        // One attempt, not the default three. The remaining two would have re-asked a question the
        // daemon has already answered.
        agent.AttemptCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(ShardStartFailureReason.HighWaterNotRunning)]
    [InlineData(ShardStartFailureReason.StartRace)]
    [InlineData(ShardStartFailureReason.Faulted)]
    [InlineData(ShardStartFailureReason.Unknown)]
    public async Task every_other_reason_still_spends_the_whole_budget(ShardStartFailureReason reason)
    {
        // The guard against over-reading the classification: only the two non-transient reasons that
        // name a standing condition short-circuit. Faulted and Unknown are NOT transient either, and
        // they must keep today's behaviour — the inner exception decides, and a retry is free to help.
        var uri = new Uri("event-subscriptions://marten/incident/all");
        var agent = new FlakyAgent(uri, int.MaxValue, failing(reason));
        var controller = controllerFor(agent);

        await Should.ThrowAsync<AgentStartingException>(() => controller.StartAgentAsync(uri));

        agent.AttemptCount.ShouldBe(3);
    }

    [Fact]
    public async Task an_unregistered_shard_reaches_the_observer_with_its_reason()
    {
        // The reason travels as the daemon's own enum rather than as text, so CritterWatch does not
        // have to parse it back out of an exception message.
        var uri = new Uri("event-subscriptions://marten/incident/all");
        var controller = controllerFor(new FlakyAgent(uri, int.MaxValue,
            failing(ShardStartFailureReason.ShardNotRegistered)));

        await Should.ThrowAsync<AgentStartingException>(() => controller.StartAgentAsync(uri));

        await _observer.Received(1)
            .AgentStartFailed(uri, ShardStartFailureReason.ShardNotRegistered, Arg.Any<Exception>());
        await _observer.DidNotReceive().AgentPaused(Arg.Any<Uri>(), Arg.Any<ShardFailure?>());
    }

    [Fact]
    public async Task a_paused_agent_is_reported_through_the_existing_paused_path()
    {
        // GH-3638 already owns "paused, and restarting it is the wrong move". A start that fails for
        // that reason joins it rather than growing a second reporting path.
        var uri = new Uri("event-subscriptions://marten/incident/all");
        var controller = controllerFor(new FlakyAgent(uri, int.MaxValue,
            failing(ShardStartFailureReason.AgentPaused)));

        await Should.ThrowAsync<AgentStartingException>(() => controller.StartAgentAsync(uri));

        await _observer.Received(1).AgentPaused(uri, null);
        await _observer.DidNotReceive()
            .AgentStartFailed(Arg.Any<Uri>(), Arg.Any<ShardStartFailureReason>(), Arg.Any<Exception>());
    }

    [Fact]
    public async Task the_report_fires_once_per_transition_not_once_per_reevaluation()
    {
        // The whole complaint is noise on every CheckAssignmentPeriod. Reporting per tick would swap
        // a retry storm for a log storm.
        var uri = new Uri("event-subscriptions://marten/incident/all");
        var controller = controllerFor(new FlakyAgent(uri, int.MaxValue,
            failing(ShardStartFailureReason.ShardNotRegistered)));

        await Should.ThrowAsync<AgentStartingException>(() => controller.StartAgentAsync(uri));
        await Should.ThrowAsync<AgentStartingException>(() => controller.StartAgentAsync(uri));
        await Should.ThrowAsync<AgentStartingException>(() => controller.StartAgentAsync(uri));

        await _observer.Received(1)
            .AgentStartFailed(uri, ShardStartFailureReason.ShardNotRegistered, Arg.Any<Exception>());
    }

    [Fact]
    public async Task the_reason_is_found_through_a_wrapping_exception()
    {
        // By the time StartAgentAsync asks, the daemon's exception has already been wrapped in an
        // AgentStartingException — and an agent family is free to wrap it again on the way out. The
        // classification has to survive both, or this whole feature is inert in production while
        // passing a test that throws it bare.
        var uri = new Uri("event-subscriptions://marten/incident/all");
        var agent = new FlakyAgent(uri, int.MaxValue, () => new InvalidOperationException("wrapped",
            new AggregateException(
                new ShardStartException("Incident:All", "no such shard",
                    ShardStartFailureReason.ShardNotRegistered))));
        var controller = controllerFor(agent);

        await Should.ThrowAsync<AgentStartingException>(() => controller.StartAgentAsync(uri));

        agent.AttemptCount.ShouldBe(1);
        await _observer.Received(1)
            .AgentStartFailed(uri, ShardStartFailureReason.ShardNotRegistered, Arg.Any<Exception>());
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
        private readonly Func<Exception> _failure;

        public FlakyAgent(Uri uri, int failuresBeforeSuccess, Func<Exception>? failure = null)
        {
            Uri = uri;
            _failuresBeforeSuccess = failuresBeforeSuccess;

            // The real thing as of JasperFx 2.76.0. This used to be a bare Exception reproducing the
            // message shape, because ShardStartException's constructors were internal to JasperFx.Events;
            // jasperfx#912 made them public, so the stand-in is gone and these tests now exercise the
            // exact type and Reason the daemon throws.
            _failure = failure ?? (() => new ShardStartException("Incident:All",
                "High-water detection is not running yet, so the shard could not be positioned.",
                ShardStartFailureReason.HighWaterNotRunning));
        }

        public int AttemptCount { get; private set; }

        public Uri Uri { get; }
        public AgentStatus Status { get; private set; } = AgentStatus.Stopped;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            AttemptCount++;
            if (AttemptCount <= _failuresBeforeSuccess)
            {
                throw _failure();
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
