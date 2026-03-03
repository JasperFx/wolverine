using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using NSubstitute;
using Shouldly;
using Wolverine.Marten.Distribution;

namespace MartenTests.Distribution;

public class event_subscription_agent_rebuild
{
    [Fact]
    public async Task rebuild_delegates_to_daemon()
    {
        var daemon = Substitute.For<IProjectionDaemon>();
        var shardName = new ShardName("Trip");
        var uri = new Uri("event-subscriptions://marten/main/localhost.postgres/trip/all");
        var agent = new EventSubscriptionAgent(uri, shardName, daemon);

        await agent.RebuildAsync(CancellationToken.None);

        await daemon.Received(1).RebuildProjectionAsync("Trip", CancellationToken.None);
    }

    [Fact]
    public async Task rebuild_passes_cancellation_token()
    {
        var daemon = Substitute.For<IProjectionDaemon>();
        var shardName = new ShardName("Distance");
        var uri = new Uri("event-subscriptions://marten/main/localhost.postgres/distance/all");
        var agent = new EventSubscriptionAgent(uri, shardName, daemon);

        using var cts = new CancellationTokenSource();
        await agent.RebuildAsync(cts.Token);

        await daemon.Received(1).RebuildProjectionAsync("Distance", cts.Token);
    }
}
