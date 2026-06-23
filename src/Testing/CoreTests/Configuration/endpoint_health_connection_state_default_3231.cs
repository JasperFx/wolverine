using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Configuration;

// GH-3231: transports that have no notion of a connection (TCP, in-memory local queues) must report
// TransportConnectionState.Unknown rather than a misleading Connected/Disconnected.
public class endpoint_health_connection_state_default_3231
{
    [Fact]
    public async Task non_connection_aware_endpoints_report_unknown()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ListenAtPort(PortFinder.GetAvailablePort());
                opts.PublishAllMessages().ToPort(PortFinder.GetAvailablePort());
            }).StartAsync();

        var snapshots = host.GetRuntime().Endpoints.CollectEndpointHealth();
        snapshots.ShouldNotBeEmpty();

        // No endpoint here exposes IReportConnectionState, so every snapshot must fall through to Unknown.
        foreach (var snapshot in snapshots)
        {
            snapshot.ConnectionState.ShouldBe(TransportConnectionState.Unknown,
                $"{snapshot.Direction} endpoint {snapshot.Uri}");
        }
    }
}
