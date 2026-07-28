using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests.Partitioning;
using Wolverine.Postgresql;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Redis.Tests;

// GH-3467: end-to-end coverage for the Redis Streams global partitioning topology.
public class global_partitioned_sharded_processing : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.UseRedisTransport(RedisContainerFixture.ConnectionString).AutoProvision();

                // The global partitioning topology forces every slot into durable mode, so the
                // host needs a real message store
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "gletters_redis");

                opts.UseShardedLetters(topology => topology.UseShardedRedisStreams("gletters", 4));
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task global_partitioned_processing_spreads_across_every_slot()
    {
        var tracked = await _host
            .TrackActivity()
            .IncludeExternalTransports()
            .Timeout(120.Seconds())
            .ExecuteAndWaitAsync(ShardedProcessing.PumpOutLetters);

        ShardedProcessing.AssertEveryShardWasUsed(tracked, "gletters", 4);
        ShardedProcessing.AssertGroupsNeverStraddleSlots();
    }
}
