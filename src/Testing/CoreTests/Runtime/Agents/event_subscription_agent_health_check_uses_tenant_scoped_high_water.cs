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
/// Regression coverage for GH-3580. A per-tenant subscription agent's Position lives in its own
/// tenant's event sequence (per-tenant event partitioning gives every tenant an independent sequence,
/// GH-3280), but CheckHealthAsync measured it against the database-wide ShardStateTracker.HighWaterMark.
/// Every quiet tenant in a busy database read as thousands of "events behind" — a tenant with zero
/// events reported the full database mark as its lag — and the stall detector's permanently true
/// "currentSequence &lt; highWaterMark" auto-restarted healthy idle agents. The health check must use
/// the inner agent's tenant-scoped high-water mark (seeded and routed per tenant since marten#4717)
/// for tenant-scoped shards.
/// </summary>
public class event_subscription_agent_health_check_uses_tenant_scoped_high_water
{
    private readonly IProjectionDaemon _daemon = Substitute.For<IProjectionDaemon>();
    private readonly ShardStateTracker _tracker = new(NullLogger.Instance);
    private readonly JasperFxSubscriptionAgent _inner = Substitute.For<JasperFxSubscriptionAgent>();

    public event_subscription_agent_health_check_uses_tenant_scoped_high_water()
    {
        _daemon.Tracker.Returns(_tracker);
        _daemon.StartAgentAsync(Arg.Any<ShardName>(), Arg.Any<CancellationToken>()).Returns(_inner);
        _inner.Status.Returns(AgentStatus.Running);
    }

    private async Task<EventSubscriptionAgent> startedAgentFor(ShardName shardName, Uri uri)
    {
        var agent = new EventSubscriptionAgent(uri, shardName, _daemon);
        await agent.StartAsync(CancellationToken.None);
        return agent;
    }

    [Fact]
    public async Task a_tenant_with_no_events_is_healthy_even_when_the_database_mark_is_far_ahead()
    {
        // The database-wide mark is way past the critical threshold, driven entirely by other tenants
        await _tracker.MarkHighWaterAsync(8900);

        // This tenant has never appended an event: no per-tenant high-water, no progression
        _inner.HighWaterMark.Returns(0);
        _inner.Position.Returns(0);

        var agent = await startedAgentFor(
            new ShardName("invoicejournalentries", "all", 4, "98123456"),
            new Uri("event-subscriptions://marten/main/db/invoicejournalentries/all/v4/98123456"));

        var result = await agent.CheckHealthAsync(new HealthCheckContext());

        // Before the fix: "Projection ... is 8900 events behind (critical threshold: 5000)"
        result.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task a_tenant_genuinely_behind_its_own_mark_is_still_reported()
    {
        await _tracker.MarkHighWaterAsync(100_000);

        // Behind by 5900 against its OWN tenant's sequence — past the critical threshold of 5000
        _inner.HighWaterMark.Returns(6000);
        _inner.Position.Returns(100);

        var agent = await startedAgentFor(
            new ShardName("invoicejournalentries", "all", 4, "98123456"),
            new Uri("event-subscriptions://marten/main/db/invoicejournalentries/all/v4/98123456"));

        var result = await agent.CheckHealthAsync(new HealthCheckContext());

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description!.ShouldContain("5900 events behind");
    }

    [Fact]
    public async Task a_caught_up_tenant_does_not_trip_the_stall_detector_on_repeated_checks()
    {
        await _tracker.MarkHighWaterAsync(8900);

        // Fully caught up within its own tenant's sequence, idle (no new events)
        _inner.HighWaterMark.Returns(120);
        _inner.Position.Returns(120);

        var agent = await startedAgentFor(
            new ShardName("invoicejournalentries", "all", 4, "98123456"),
            new Uri("event-subscriptions://marten/main/db/invoicejournalentries/all/v4/98123456"));

        // Before the fix, "Position < database mark" read as pending work forever, so an idle tenant
        // racked up consecutive stalls and got auto-restarted. Repeated checks must stay healthy.
        for (var i = 0; i < 5; i++)
        {
            var result = await agent.CheckHealthAsync(new HealthCheckContext());
            result.Status.ShouldBe(HealthStatus.Healthy);
        }
    }

    [Fact]
    public async Task a_store_global_agent_still_measures_against_the_database_wide_mark()
    {
        await _tracker.MarkHighWaterAsync(8900);

        // Store-global shard (TenantId null): the database-wide tracker mark is the right ceiling,
        // and the inner agent's default HighWaterMark of 0 must NOT suppress the alert
        _inner.Position.Returns(100);

        var agent = await startedAgentFor(
            new ShardName("invoicejournalentries"),
            new Uri("event-subscriptions://marten/main/db/invoicejournalentries/all/v4"));

        var result = await agent.CheckHealthAsync(new HealthCheckContext());

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description!.ShouldContain("8800 events behind");
    }
}
