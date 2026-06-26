using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.Pulsar.Tests;

// GH-3231: PulsarListener tracks the DotPulsar consumer state in the background and implements IReportConnectionState,
// so a healthy listener surfaces Connected on EndpointHealthSnapshot once its consumer becomes Active.
[Collection("pulsar")]
public class connection_state_3231
{
    [Fact]
    public async Task healthy_pulsar_listener_reports_connected()
    {
        var topic = $"persistent://public/default/connstate-{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
                opts.ListenToPulsarTopic(topic);
            }).StartAsync();

        var state = await ConnectionStateTestHelpers.WaitForListenerConnectionStateAsync(
            host, "pulsar", TransportConnectionState.Connected);

        state.ShouldBe(TransportConnectionState.Connected);
    }
}
