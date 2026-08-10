using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Shouldly;
using Wolverine.Runtime.Agents;
using Xunit;
using IProjectionDaemon = JasperFx.Events.Daemon.IProjectionDaemon;
using JasperFxSubscriptionAgent = JasperFx.Events.Daemon.ISubscriptionAgent;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// Coverage for the auto-restart branch of <see cref="EventSubscriptionAgent.CheckHealthAsync" />, which
/// until now had none: reaching it takes three consecutive stalls sixty seconds apart, and the clock was
/// <c>DateTimeOffset.UtcNow</c> read inline. With <see cref="EventSubscriptionAgent.TimeProvider" /> the
/// branch is reachable in milliseconds.
///
/// The distinction under test is the one an operator acts on. A failure bound to a specific event will
/// reproduce on every start, so restarting is churn that also resets their view of when it broke
/// (GH-3638) — those are surfaced and left alone. A <see cref="ShardFailureCategory.Other" /> failure is
/// what auto-restart exists for, and is retried.
///
/// The last test is skipped and states the behaviour GH-3888 asks for: that retry is node-local, so a
/// node-level fault (memory pressure, a bad disk) reproduces on every attempt while a healthy peer
/// advertising the same capability never gets a chance at the shard.
/// </summary>
public class stalled_agent_auto_restart
{
    private const long HighWater = 10_000;

    private readonly IProjectionDaemon _daemon = Substitute.For<IProjectionDaemon>();
    private readonly JasperFxSubscriptionAgent _inner = Substitute.For<JasperFxSubscriptionAgent>();
    private readonly FrozenClock _clock = new(DateTimeOffset.UtcNow);
    private readonly EventSubscriptionAgent _agent;
    private int _restarts;

    public stalled_agent_auto_restart()
    {
        _daemon.StartAgentAsync(Arg.Any<ShardName>(), Arg.Any<CancellationToken>()).Returns(_inner);
        _agent = new EventSubscriptionAgent(
            new Uri("event-subscriptions://marten/invoices/all/v16/01009333"),
            new ShardName("invoices", "all", 16, "01009333"), _daemon) { TimeProvider = _clock };
        _agent.OnRestarted = (_, _, _) => Interlocked.Increment(ref _restarts);
    }

    private static ShardFailure FailureOf(ShardFailureCategory category) => new()
    {
        Category = category,
        ExceptionType = "System.OutOfMemoryException",
        RootExceptionType = "System.OutOfMemoryException",
        Message = "Exception of type 'System.OutOfMemoryException' was thrown.",
        Detail = "System.OutOfMemoryException: Exception of type 'System.OutOfMemoryException' was thrown.",
        OccurredAt = DateTimeOffset.UtcNow
    };

    private Task<HealthCheckResult> checkAsync()
        => _agent.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

    /// <summary>
    /// Park the agent behind the high-water mark and run the clock past StallTimeout often enough to trip
    /// MaxConsecutiveStallsBeforeRestart. Returns the result of the check that trips it.
    /// </summary>
    private async Task<HealthCheckResult> stallUntilRestartAsync()
    {
        _inner.Status.Returns(AgentStatus.Running);
        // Just behind, deliberately: a bigger gap trips the warning/critical branches first and the
        // health check returns before the stall detector is ever consulted.
        _inner.Position.Returns(HighWater - 100);
        _inner.HighWaterMark.Returns(HighWater);

        await _agent.StartAsync(CancellationToken.None);
        await checkAsync();                       // anchors the first observation

        HealthCheckResult result = default;
        for (var i = 0; i < 3; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(61));
            result = await checkAsync();
        }

        return result;
    }

    [Fact]
    public async Task an_infrastructure_failure_is_retried_because_that_is_what_auto_restart_is_for()
    {
        _inner.Failure.Returns(FailureOf(ShardFailureCategory.Other));

        var result = await stallUntilRestartAsync();

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description.ShouldNotBeNull().ShouldContain("Attempting auto-restart");
    }

    [Fact]
    public async Task a_failure_bound_to_one_event_is_surfaced_instead_of_restart_looped()
    {
        // GH-3638: restarting re-runs the identical failure and resets the operator's view every time.
        _inner.Failure.Returns(FailureOf(ShardFailureCategory.ApplyEvent));

        var result = await stallUntilRestartAsync();

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description.ShouldNotBeNull().ShouldContain("Auto-restart suppressed");
    }

    [Fact]
    public async Task an_agent_that_is_keeping_up_never_reaches_the_restart_branch()
    {
        // The counterweight to the two above: without this, a test that always trips the detector would
        // pass just as happily against a detector that fires unconditionally.
        _inner.Status.Returns(AgentStatus.Running);
        _inner.Position.Returns(HighWater);
        _inner.HighWaterMark.Returns(HighWater);

        await _agent.StartAsync(CancellationToken.None);
        await checkAsync();

        for (var i = 0; i < 5; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(61));
            (await checkAsync()).Status.ShouldBe(HealthStatus.Healthy);
        }
    }

    /// <summary>
    /// Run the clock through stalled health checks. The auto-restart itself runs on a background task,
    /// so whenever a check reports that it dispatched one, wait for that restart to complete before the
    /// next check — the loop then observes a settled agent every iteration instead of racing it.
    /// </summary>
    private async Task<HealthCheckResult> stallThroughChecksAsync(int iterations)
    {
        HealthCheckResult result = default;
        for (var i = 0; i < iterations; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(61));
            var restartsBefore = Volatile.Read(ref _restarts);
            result = await checkAsync();

            if (result.Description?.Contains("Attempting auto-restart") == true)
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (Volatile.Read(ref _restarts) <= restartsBefore && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(10);
                }
            }
        }

        return result;
    }

    [Fact]
    public async Task a_failure_that_survives_local_restarts_stops_being_retried_locally()
    {
        // Measured on a 512-database cluster: the warming fleet ran out of memory holding 53 agents of a
        // projection version the serving fleet also runs. Every retry landed on the same starved node and
        // failed identically; the serving fleet advertised the capability, was healthy, and never received
        // them. Those read models sat frozen for sixteen minutes with a capable node beside them.
        //
        // GH-3888: after MaxLocalRestartsBeforeRelease restarts with no observed progress, the agent stops
        // being retried here and flags itself exhausted, which is what asks NodeAgentController's
        // failed-agent sweep to release its assignment so the leader can place it on a capable peer.
        _inner.Failure.Returns(FailureOf(ShardFailureCategory.Other));

        await stallUntilRestartAsync();
        var last = await stallThroughChecksAsync(20);

        last.Status.ShouldBe(HealthStatus.Unhealthy);
        last.Description.ShouldNotBeNull().ShouldContain("exhausted");
        _agent.LocalRestartsExhausted.ShouldBeTrue();

        // Before GH-3888 this climbed for as long as the node stayed broken; the budget caps it.
        _restarts.ShouldBeLessThanOrEqualTo(_agent.MaxLocalRestartsBeforeRelease);
    }

    [Fact]
    public async Task observed_progress_refunds_the_local_restart_budget()
    {
        _inner.Failure.Returns(FailureOf(ShardFailureCategory.Other));

        await stallUntilRestartAsync();
        await stallThroughChecksAsync(20);
        _agent.LocalRestartsExhausted.ShouldBeTrue();

        // The shard advances — whatever was starving this node has passed, and restarting here worked
        // after all. The budget refunds and the agent goes back to ordinary stall tracking.
        _inner.Position.Returns(HighWater - 50);
        _clock.Advance(TimeSpan.FromSeconds(61));
        await checkAsync();

        _agent.LocalRestartsExhausted.ShouldBeFalse();

        // And a NEW stall episode gets a fresh budget: the restart branch fires again rather than
        // staying latched on the exhausted state.
        var restartsBefore = _restarts;
        var tripped = await stallThroughChecksAsync(4);
        tripped.Description.ShouldNotBeNull();
        _restarts.ShouldBeGreaterThan(restartsBefore);
    }

    [Fact]
    public async Task reset_local_restart_budget_re_arms_local_restarts()
    {
        _inner.Failure.Returns(FailureOf(ShardFailureCategory.Other));

        await stallUntilRestartAsync();
        await stallThroughChecksAsync(20);
        _agent.LocalRestartsExhausted.ShouldBeTrue();

        // What NodeAgentController calls when it declines the release because no live peer advertises
        // the capability: local retries remain the least-bad option and must keep happening.
        _agent.ResetLocalRestartBudget();

        _agent.LocalRestartsExhausted.ShouldBeFalse();

        var restartsBefore = _restarts;
        await stallThroughChecksAsync(4);
        _restarts.ShouldBeGreaterThan(restartsBefore);
    }

    [Fact]
    public async Task a_nonpositive_budget_disables_release_and_keeps_unbounded_local_retries()
    {
        // The pre-GH-3888 behavior, kept reachable as a kill switch through
        // DurabilitySettings.MaxLocalAgentRestartsBeforeRelease <= 0.
        _agent.MaxLocalRestartsBeforeRelease = 0;
        _inner.Failure.Returns(FailureOf(ShardFailureCategory.Other));

        await stallUntilRestartAsync();
        await stallThroughChecksAsync(20);

        _agent.LocalRestartsExhausted.ShouldBeFalse();
        _restarts.ShouldBeGreaterThan(3);
    }

    /// <summary>A clock the test moves by hand; the stall detector reads nothing else.</summary>
    private sealed class FrozenClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
