using Microsoft.Extensions.Hosting;
using Shouldly;
using TestingSupport;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace Wolverine.MQTT.Tests;

public class broadcast_to_topic_by_user_logic: IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _sender;
    private IHost _receiver;

    public broadcast_to_topic_by_user_logic(ITestOutputHelper output)
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

    public LocalMqttBroker Broker { get; set; }

    public async Task DisposeAsync()
    {
        await Broker.StopAsync();
        await _sender.StopAsync();
        await _receiver.StopAsync();
    }
    
}