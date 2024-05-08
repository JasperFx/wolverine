using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TestingSupport;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace Wolverine.MQTT.Tests;

public class ack_smoke_tests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _sender;
    private IHost _receiver;
    private LocalMqttBroker Broker;

    public ack_smoke_tests(ITestOutputHelper output)
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
                opts.UseMqttWithLocalBroker(port)
                    // All messages are retained
                    .ConfigureSenders(x => x.RetainMessages());
                opts.Discovery.DisableConventionalDiscovery();
                opts.Policies.DisableConventionalLocalRouting();

            }).StartAsync();

        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseMqttWithLocalBroker(port);
                opts.ListenToMqttTopic("red").RetainMessages();
            }).StartAsync();
    }

    [Fact]
    public async Task send_zero_message()
    {
        var bus = _sender.Services.GetRequiredService<IMessageBus>();
        await bus.BroadcastToTopicAsync("red", new ZeroMessage("Zero"));

        await Task.Delay(2.Seconds());
    }

    [Fact]
    public async Task send_ack_message()
    {
        var bus = _sender.Services.GetRequiredService<IMessageBus>();
        await bus.BroadcastToTopicAsync("red", new TriggerZero("red"));

        await Task.Delay(2.Seconds());
    }

    public async Task DisposeAsync()
    {
        await _sender.StopAsync();
        await _receiver.StopAsync();
    }
}

public record ZeroMessage(string Name);

public record TriggerZero(string Topic);

public static class ZeroMessageHandler
{
    #region sample_ack_mqtt_topic

    public static AckMqttTopic Handle(ZeroMessage message)
    {
        // "Zero out" the topic that the original message was received from
        return new AckMqttTopic();
    }

    public static ClearMqttTopic Handle(TriggerZero message)
    {
        // "Zero out" the designated topic
        return new ClearMqttTopic("red");
    }

    #endregion
}