using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.Nats.Tests;

// GH-3231: NatsListener implements IReportConnectionState off the NATS connection's ConnectionState, so a healthy
// listener surfaces Connected on EndpointHealthSnapshot.
public class connection_state_3231 : IClassFixture<NatsContainerFixture>
{
    private readonly NatsContainerFixture _fixture;

    public connection_state_3231(NatsContainerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task healthy_nats_listener_reports_connected()
    {
        var subject = $"connstate.{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseNats(_fixture.ConnectionString);
                opts.ListenToNatsSubject(subject);
            }).StartAsync();

        var state = await ConnectionStateTestHelpers.WaitForListenerConnectionStateAsync(
            host, "nats", TransportConnectionState.Connected);

        state.ShouldBe(TransportConnectionState.Connected);
    }
}
