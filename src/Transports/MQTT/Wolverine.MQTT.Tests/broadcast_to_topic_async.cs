using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit.Abstractions;

namespace Wolverine.MQTT.Tests;

public class broadcast_to_topic_async : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _sender;
    private IHost _receiver;

    public broadcast_to_topic_async(ITestOutputHelper output)
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

        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseMqttWithLocalBroker(port);
                opts.ListenToMqttTopic("incoming/one").RetainMessages();
            }).StartAsync();

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

public class ColorMessage
{
    public ColorMessage()
    {
    }

    public ColorMessage(string color)
    {
        Color = color;
    }

    public string Color { get; set; }
}

public class SpecialColorMessage : ColorMessage;

public static class ColorMessageHandler
{
    public static void Handle(ColorMessage message)
    {
        Debug.WriteLine("Got " + message.Color);
    }

    public static void Handle(SpecialColorMessage message)
    {
        Debug.WriteLine("Got " + message.Color);
    }
}