using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Transports;
using Wolverine.Util;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.MQTT.Tests;

// GH-3231: MqttListener implements IReportConnectionState off the managed MQTT client's IsConnected, so a healthy
// listener surfaces Connected on EndpointHealthSnapshot.
[Collection("acceptance")]
public class connection_state_3231 : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _host = null!;
    private LocalMqttBroker _broker = null!;

    public connection_state_3231(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        var port = PortFinder.GetAvailablePort();
        _broker = new LocalMqttBroker(port) { Logger = new XUnitLogger(_output, "MQTT") };
        await _broker.StartAsync();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseMqttWithLocalBroker(port);
                opts.ListenToMqttTopic("connstate");
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        await _broker.StopAsync();
    }

    [Fact]
    public async Task healthy_mqtt_listener_reports_connected()
    {
        var state = await ConnectionStateTestHelpers.WaitForListenerConnectionStateAsync(
            _host, "mqtt", TransportConnectionState.Connected);

        state.ShouldBe(TransportConnectionState.Connected);
    }
}
