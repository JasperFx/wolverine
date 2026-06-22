using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.MQTT.Internals;
using Wolverine.Runtime;
using Wolverine.Util;

namespace Wolverine.MQTT.Tests;

public class InlineComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    public static int Number = 0;

    public InlineComplianceFixture() : base(new Uri("mqtt://topic/receiver"), 120)
    {
    }

    public async Task InitializeAsync()
    {
        var port = PortFinder.GetAvailablePort();

        var number = ++Number;
        var receiverTopic = "receiver-" + number;
        var senderTopic = "sender-" + number;

        Broker = new LocalMqttBroker(port)
        {

        };

        await Broker.StartAsync();

        OutboundAddress = new Uri("mqtt://topic/" + receiverTopic);

        await SenderIs(opts =>
        {
            opts.UseMqttWithLocalBroker(port);

            opts.ListenToMqttTopic(senderTopic);

            opts.PublishAllMessages().ToMqttTopic(receiverTopic).SendInline();
        });

        await ReceiverIs(opts =>
        {
            opts.UseMqttWithLocalBroker(port);

            opts.ListenToMqttTopic(receiverTopic).Named("receiver").ProcessInline();
        });
    }

    public LocalMqttBroker Broker { get; private set; } = null!;

    public new async Task DisposeAsync()
    {
        await Broker.StopAsync();
        await Broker.DisposeAsync();
    }
}

[Collection("acceptance")]
public class InlineSendingAndReceivingCompliance : TransportCompliance<InlineComplianceFixture>
{
    [Fact]
    public void has_response_topic_automatically()
    {
        var options = theSender.Services.GetRequiredService<IWolverineRuntime>().Options;
        var transport = options.Transports
            .GetOrCreate<MqttTransport>();

        // Solo mode keys the reply topic on the unique node id (not the always-1 assigned node
        // number) so multiple Solo services on one broker don't collide. See #3189.
        transport.ResponseTopic.ShouldBe("wolverine/response/" + options.UniqueNodeId.ToString("N"));

        transport.ReplyEndpoint().ShouldBeOfType<MqttTopic>().TopicName.ShouldBe(transport.ResponseTopic);
    }

    [Fact]
    public void topics_are_all_retain_equals_false()
    {
        var options = theSender.Services.GetRequiredService<IWolverineRuntime>().Options;
        var transport = options.Transports
            .GetOrCreate<MqttTransport>();

        foreach (var topic in transport.Topics)
        {
            topic.Retain.ShouldBeFalse();
        }
    }
}