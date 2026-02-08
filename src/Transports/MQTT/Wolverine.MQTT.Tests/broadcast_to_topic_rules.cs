using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Attributes;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit.Abstractions;

namespace Wolverine.MQTT.Tests;

[Collection("acceptance")]
public class broadcast_to_topic_rules : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _sender;
    private IHost _receiver;

    public broadcast_to_topic_rules(ITestOutputHelper output)
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

                opts.PublishAllMessages().ToMqttTopics();

                opts.ServiceName = "sender";
            }).StartAsync();

        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseMqttWithLocalBroker(port);
                opts.ListenToMqttTopic("red").RetainMessages();
                opts.ListenToMqttTopic("green").RetainMessages();
                opts.ListenToMqttTopic("blue").RetainMessages();
                opts.ListenToMqttTopic("purple").RetainMessages();

                opts.ServiceName = "receiver";
            }).StartAsync();

    }

    [Fact]
    public async Task route_by_derived_topics_1()
    {
        var session = await _sender
            .TrackActivity()
            .AlsoTrack(_receiver)
            .WaitForMessageToBeReceivedAt<RedMessage>(_receiver)
            .PublishMessageAndWaitAsync(new RedMessage("one"));

        session.Received.SingleEnvelope<RedMessage>()
            .Destination.ShouldBe(new Uri("mqtt://topic/red"));
    }

    [Fact]
    public async Task route_by_derived_topics_2()
    {
        var session = await _sender
            .TrackActivity()
            .AlsoTrack(_receiver)
            .WaitForMessageToBeReceivedAt<GreenMessage>(_receiver)
            .PublishMessageAndWaitAsync(new GreenMessage("one"));

        session.Received.SingleEnvelope<GreenMessage>()
            .Destination.ShouldBe(new Uri("mqtt://topic/green"));
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

[Topic("red")]
public record RedMessage(string Name);

[Topic("green")]
public record GreenMessage(string Name);

public static class ColoredMessageHandler
{
    public static void Handle(RedMessage m) => Debug.WriteLine("Got red " + m.Name);
    public static void Handle(GreenMessage m) => Debug.WriteLine("Got green " + m.Name);
}