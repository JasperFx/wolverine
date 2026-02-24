using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit.Abstractions;

namespace Wolverine.MQTT.Tests;

public class MosquittoBufferedComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    public static int Number = 0;

    public MosquittoBufferedComplianceFixture() : base(new Uri("mqtt://topic/receiver"), 120)
    {
    }

    public async Task InitializeAsync()
    {
        var number = ++Number;
        var receiverTopic = "mosquitto-receiver-" + number;
        var senderTopic = "mosquitto-sender-" + number;

        OutboundAddress = new Uri("mqtt://topic/" + receiverTopic);

        await SenderIs(opts =>
        {
            opts.UseMqtt(mqtt =>
            {
                mqtt.WithClientOptions(client => client.WithTcpServer("localhost", 1883));
            });

            opts.ListenToMqttTopic(senderTopic).RetainMessages();
            opts.PublishAllMessages().ToMqttTopic(receiverTopic).RetainMessages().BufferedInMemory();
        });

        await ReceiverIs(opts =>
        {
            opts.UseMqtt(mqtt =>
            {
                mqtt.WithClientOptions(client => client.WithTcpServer("localhost", 1883));
            });

            opts.ListenToMqttTopic(receiverTopic).Named("receiver").RetainMessages().BufferedInMemory();
        });
    }

    public new async Task DisposeAsync()
    {
        // Nothing extra to dispose; Mosquitto runs in Docker
    }
}

[Collection("mosquitto")]
public class MosquittoBufferedCompliance : TransportCompliance<MosquittoBufferedComplianceFixture>;

/// <summary>
/// GH-2213: Verifies that shared subscriptions with a specific topic work
/// against a real Mosquitto broker.
/// </summary>
[Collection("mosquitto")]
public class mosquitto_shared_subscription_specific_topic : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _sender;
    private IHost _receiver;

    public mosquitto_shared_subscription_specific_topic(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseMqtt(mqtt =>
                {
                    mqtt.WithClientOptions(client => client.WithTcpServer("localhost", 1883));
                });
                opts.Policies.DisableConventionalLocalRouting();
            }).StartAsync();

        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseMqtt(mqtt =>
                {
                    mqtt.WithClientOptions(client => client.WithTcpServer("localhost", 1883));
                });
                opts.ListenToMqttTopic("incoming/one", "group1").RetainMessages();
            }).StartAsync();
    }

    [Fact]
    public async Task send_to_shared_topic_and_receive_with_mosquitto()
    {
        var tracked = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(m => m.BroadcastToTopicAsync("incoming/one", new ColorMessage("green")));

        var received = tracked.Received.SingleMessage<ColorMessage>();
        received.Color.ShouldBe("green");
    }

    public async Task DisposeAsync()
    {
        await _sender.StopAsync();
        await _receiver.StopAsync();
    }
}

/// <summary>
/// GH-2213: Verifies that shared subscriptions with a wildcard topic work
/// against a real Mosquitto broker.
/// </summary>
[Collection("mosquitto")]
public class mosquitto_shared_subscription_with_wildcard : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _sender;
    private IHost _receiver;

    public mosquitto_shared_subscription_with_wildcard(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseMqtt(mqtt =>
                {
                    mqtt.WithClientOptions(client => client.WithTcpServer("localhost", 1883));
                });
                opts.Policies.DisableConventionalLocalRouting();
            }).StartAsync();

        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseMqtt(mqtt =>
                {
                    mqtt.WithClientOptions(client => client.WithTcpServer("localhost", 1883));
                });
                // GH-2213: shared subscription with wildcard
                opts.ListenToMqttTopic("incoming/+", "workers").RetainMessages();
            }).StartAsync();
    }

    [Fact]
    public async Task can_receive_with_shared_subscription_wildcard()
    {
        var tracked = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(m => m.BroadcastToTopicAsync("incoming/orders", new ColorMessage("red")));

        var received = tracked.Received.SingleMessage<ColorMessage>();
        received.Color.ShouldBe("red");
    }

    public async Task DisposeAsync()
    {
        await _sender.StopAsync();
        await _receiver.StopAsync();
    }
}
