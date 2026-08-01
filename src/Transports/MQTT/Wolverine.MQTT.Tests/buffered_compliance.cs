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

    public async ValueTask InitializeAsync()
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

    public LocalMqttBroker Broker { get; private set; } = null!;

    // AfterDisposeAsync, not a `new DisposeAsync`: TransportCompliance<T> disposes the fixture through
    // the statically-bound base method, so a hiding override never runs -- meaning this broker was never
    // stopped and leaked its listening socket for the life of the test process. See #3763.
    protected override async Task AfterDisposeAsync()
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

        // Solo mode keys the reply topic on the unique node id (not the always-1 assigned node
        // number) so multiple Solo services on one broker don't collide. See #3189.
        transport.ResponseTopic.ShouldBe("wolverine/response/" + options.UniqueNodeId.ToString("N"));

        transport.ReplyEndpoint().ShouldBeOfType<MqttTopic>().TopicName.ShouldBe(transport.ResponseTopic);
    }
}