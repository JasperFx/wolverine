using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Configuration;

// GH-3236: listeners that don't run a managed BackgroundReceiveLoop (TCP accept loop, in-memory local queues) must
// report ReceiveLoopStatus.Unknown with no heartbeat rather than a misleading status.
public class receive_loop_health_default_3236
{
    [Fact]
    public async Task non_loop_listeners_report_unknown()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ListenAtPort(PortFinder.GetAvailablePort());
            }).StartAsync();

        var snapshots = host.GetRuntime().Endpoints.CollectEndpointHealth();
        snapshots.ShouldNotBeEmpty();

        foreach (var snapshot in snapshots)
        {
            snapshot.ReceiveLoopStatus.ShouldBe(ReceiveLoopStatus.Unknown, $"{snapshot.Direction} {snapshot.Uri}");
            snapshot.LastReceiveLoopActivityAt.ShouldBeNull();
        }
    }
}
