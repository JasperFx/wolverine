using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.MQTT.Internals;
using Wolverine.Runtime;
using Wolverine.Util;

namespace Wolverine.MQTT.Tests;

public class BufferedComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    public static int Number = 0;

    public BufferedComplianceFixture() : base(new Uri("mqtt://topic/receiver"), 120)
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

            opts.ListenToMqttTopic(senderTopic).RetainMessages();

            opts.PublishAllMessages().ToMqttTopic(receiverTopic).RetainMessages().BufferedInMemory();
        });

        await ReceiverIs(opts =>
        {
            opts.UseMqttWithLocalBroker(port);

            opts.ListenToMqttTopic(receiverTopic).Named("receiver").RetainMessages().BufferedInMemory();
        });
    }

    public LocalMqttBroker Broker { get; private set; }

    public new async Task DisposeAsync()
    {
        await Broker.StopAsync();
        await Broker.DisposeAsync();
    }
}

[Collection("acceptance")]
public class BufferedSendingAndReceivingCompliance : TransportCompliance<BufferedComplianceFixture>
{
    [Fact]
    public async Task has_response_topic_automatically()
    {
        var options = theSender.Services.GetRequiredService<IWolverineRuntime>().Options;
        var transport = options.Transports
            .GetOrCreate<MqttTransport>();

        transport.ResponseTopic.ShouldBe("wolverine/response/" + options.Durability.AssignedNodeNumber);

        transport.ReplyEndpoint().ShouldBeOfType<MqttTopic>().TopicName.ShouldBe(transport.ResponseTopic);
    }
}