using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Runtime.Agents;
using Xunit;
using IProjectionDaemon = JasperFx.Events.Daemon.IProjectionDaemon;
using JasperFxSubscriptionAgent = JasperFx.Events.Daemon.ISubscriptionAgent;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-5716. Marten 10, Polecat 6 and Fisher 2 park a failing projection identity by default and keep the rest of the
/// shard running. When Wolverine runs the shard as its own agent, its health check reports the shard Degraded while
/// anything is parked, and Unhealthy when the shard paused at its parking limit. Same shape as the stores' own
/// parked-identity health checks
/// </summary>
public class parked_identities_health_check_5716
{
    private readonly IProjectionDaemon _daemon = Substitute.For<IProjectionDaemon>();
    private readonly ShardStateTracker _tracker = new(NullLogger.Instance);
    private readonly JasperFxSubscriptionAgent _inner = Substitute.For<JasperFxSubscriptionAgent>();
    private readonly EventSubscriptionAgent _agent;

    public parked_identities_health_check_5716()
    {
        _daemon.Tracker.Returns(_tracker);
        _daemon.StartAgentAsync(Arg.Any<ShardName>(), Arg.Any<CancellationToken>()).Returns(_inner);
        _agent = new EventSubscriptionAgent(new Uri("event-subscriptions://marten/main/trips/all"), new ShardName("Trips"),
            _daemon);
    }

    private Task<HealthCheckResult> checkAsync()
        => _agent.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

    private async Task caughtUpAndRunning()
    {
        await _tracker.MarkHighWaterAsync(500);
        _inner.Status.Returns(AgentStatus.Running);
        _inner.Position.Returns(500);
        await _agent.StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task the_parked_count_reads_through_to_the_running_shard()
    {
        // Nothing to read through to before the shard starts
        _agent.ParkedCount.ShouldBe(0);

        _inner.ParkedCount.Returns(4);
        await _agent.StartAsync(TestContext.Current.CancellationToken);

        _agent.ParkedCount.ShouldBe(4);
    }

    [Fact]
    public async Task a_shard_with_nothing_parked_is_healthy()
    {
        _inner.ParkedCount.Returns(0);
        await caughtUpAndRunning();

        (await checkAsync()).Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task a_shard_with_parked_identities_is_degraded()
    {
        _inner.ParkedCount.Returns(3);
        await caughtUpAndRunning();

        var result = await checkAsync();

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description.ShouldBe(
            "Projection event-subscriptions://marten/main/trips/all has 3 parked identities. Replay or discard them with the 'projections parked' command or CritterWatch");
    }

    [Fact]
    public async Task a_shard_paused_at_its_parking_limit_is_unhealthy()
    {
        _inner.Status.Returns(AgentStatus.Paused);
        _inner.ParkedCount.Returns(100);
        _inner.Failure.Returns(new ShardFailure
        {
            Category = ShardFailureCategory.ParkedLimitReached,
            ExceptionType = typeof(ParkedLimitReachedException).FullName!,
            RootExceptionType = typeof(ParkedLimitReachedException).FullName!,
            Message = "The shard already holds 100 parked identities",
            Detail = "JasperFx.Events.Daemon.ParkedLimitReachedException: The shard already holds 100 parked identities",
            OccurredAt = DateTimeOffset.UtcNow
        });
        await _agent.StartAsync(TestContext.Current.CancellationToken);

        var result = await checkAsync();

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description!.ShouldStartWith(
            "Projection event-subscriptions://marten/main/trips/all is Paused at its parking limit with 100 identities parked");
        result.Description.ShouldContain("MaxParkedIdentitiesPerShard");
    }
}
