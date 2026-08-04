using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;
namespace Wolverine.MQTT.Tests;

// GH-3763: route_by_derived_topics_2 costs one retry in CIMQTT5, accepted on purpose and deliberately
// NOT tagged Category=Flaky. It fails with the same
// `MqttCommunicationException : Broken pipe` as listen_with_topic_wildcards.broadcast, in the same
// run -- see the note on that class for the three candidates already eliminated and where the next
// evidence will come from. Two classes, one symptom: treat them as one investigation, not two.
[Collection("acceptance")]
public class broadcast_to_topic_by_user_logic: IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _sender = null!;
    private IHost _receiver = null!;

    public broadcast_to_topic_by_user_logic(ITestOutputHelper output)
    {
        _output = output;
    }

        public async ValueTask InitializeAsync()
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

                opts.PublishMessagesToMqttTopic<ColorMessage>(x => x.Color.ToLower());

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
            .WaitForMessageToBeReceivedAt<ColorMessage>(_receiver)
            .PublishMessageAndWaitAsync(new ColorMessage{Color = "red"});

        session.Received.SingleEnvelope<ColorMessage>()
            .Destination.ShouldBe(new Uri("mqtt://topic/red"));
    }

    [Fact]
    public async Task route_by_derived_topics_2()
    {
        var session = await _sender
            .TrackActivity()
            .AlsoTrack(_receiver)
            .WaitForMessageToBeReceivedAt<SpecialColorMessage>(_receiver)
            .PublishMessageAndWaitAsync(new SpecialColorMessage{Color = "green"});

        session.Received.SingleEnvelope<SpecialColorMessage>()
            .Destination.ShouldBe(new Uri("mqtt://topic/green"));
    }

    public LocalMqttBroker Broker { get; set; } = null!;

    public async ValueTask DisposeAsync()
    {
        await Broker.StopAsync();
        await Broker.DisposeAsync();
        await _sender.StopAsync();
        _sender.Dispose();
        await _receiver.StopAsync();
        _receiver.Dispose();
    }
}