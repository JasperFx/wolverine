using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using NSubstitute;
using Shouldly;
using Wolverine.Polecat.Distribution;
using Wolverine.Runtime.Agents;

namespace PolecatTests.Distribution;

public class event_subscription_agent_rebuild
{
    [Fact]
    public async Task rebuild_delegates_to_daemon()
    {
        var daemon = Substitute.For<IProjectionDaemon>();
        var shardName = new ShardName("Trip");
        var uri = new Uri("event-subscriptions://Polecat/SqlServer/localhost.sqlserver/trip/all");
        var agent = new EventSubscriptionAgent(uri, shardName, daemon);

        await agent.RebuildAsync(CancellationToken.None);

        // The shared EventSubscriptionAgent passes the shard's tenant (null for store-global) so a
        // per-tenant agent rebuilds only its partition; store-global shards rebuild tenant-less.
        await daemon.Received(1).RebuildProjectionAsync("Trip", null, CancellationToken.None);
    }

    [Fact]
    public async Task rebuild_passes_cancellation_token()
    {
        var daemon = Substitute.For<IProjectionDaemon>();
        var shardName = new ShardName("Distance");
        var uri = new Uri("event-subscriptions://Polecat/SqlServer/localhost.sqlserver/distance/all");
        var agent = new EventSubscriptionAgent(uri, shardName, daemon);

        using var cts = new CancellationTokenSource();
        await agent.RebuildAsync(cts.Token);

        await daemon.Received(1).RebuildProjectionAsync("Distance", null, cts.Token);
    }
}
