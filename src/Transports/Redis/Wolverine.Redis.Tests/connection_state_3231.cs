using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.Redis.Tests;

// GH-3231: RedisStreamListener implements IReportConnectionState off the StackExchange.Redis multiplexer's
// IsConnected, so a healthy listener surfaces Connected on EndpointHealthSnapshot.
public class connection_state_3231
{
    [Fact]
    public async Task healthy_redis_listener_reports_connected()
    {
        var streamKey = $"connstate-{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRedisTransport(RedisContainerFixture.ConnectionString).AutoProvision();
                opts.ListenToRedisStream(streamKey, "g1").BlockTimeout(100.Milliseconds());
            }).StartAsync();

        var state = await ConnectionStateTestHelpers.WaitForListenerConnectionStateAsync(
            host, "redis", TransportConnectionState.Connected);

        state.ShouldBe(TransportConnectionState.Connected);
    }
}
