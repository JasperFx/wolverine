using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit.Abstractions;

namespace Wolverine.MQTT.Tests;

[Collection("acceptance")]
public class listen_with_emqx_shared_group_topic : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _sender;
    private IHost _receiver;

    public listen_with_emqx_shared_group_topic(ITestOutputHelper output)
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
                opts.ListenToMqttTopic("incoming/one", "group1").RetainMessages();
            }).StartAsync();

        #endregion
    }

    [Fact]
    public async Task send_to_shared_topic_and_receive()
    {
        var tracked = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(30.Seconds())
            // The message is published to the topic *without* the $share prefix
            .ExecuteAndWaitAsync(m => m.BroadcastToTopicAsync("incoming/one", new ColorMessage("green")));

        // The receiver listening on "$share/group1/incoming/one" should receive the message
        var received = tracked.Received.SingleMessage<ColorMessage>();
        received.Color.ShouldBe("green");
    }

    public LocalMqttBroker Broker { get; set; }

    public async Task DisposeAsync()
    {
        await Broker.StopAsync();
        await Broker.DisposeAsync();
        await _sender.StopAsync();
        await _receiver.StopAsync();
    }
}