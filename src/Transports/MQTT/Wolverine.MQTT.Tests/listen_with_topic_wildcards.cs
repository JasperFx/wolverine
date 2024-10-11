using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit.Abstractions;

namespace Wolverine.MQTT.Tests;

public class listen_with_topic_wildcards : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _sender;
    private IHost _receiver;

    public listen_with_topic_wildcards(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        var port = PortFinder.GetAvailablePort();


        Broker = new LocalMqttBroker(port)
        {
            Logger = new XUnitLogger( _output, "MQTT")
        };

        await Broker.StartAsync();

        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseMqttWithLocalBroker(port);
                opts.Policies.DisableConventionalLocalRouting();
            }).StartAsync();

        #region sample_listen_to_mqtt_topic_filter

        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseMqttWithLocalBroker(port);
                opts.ListenToMqttTopic("incoming/#").RetainMessages();
            }).StartAsync();

        #endregion
    }

    [Fact]
    public async Task broadcast()
    {
        var session = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(m => m.BroadcastToTopicAsync("incoming/one", new ColorMessage("blue")));

        var received = session.Received.SingleMessage<ColorMessage>();
        received.Color.ShouldBe("blue");
    }

    public LocalMqttBroker Broker { get; set; }

    public async Task DisposeAsync()
    {
        await Broker.StopAsync();
        await _sender.StopAsync();
        await _receiver.StopAsync();
    }
}