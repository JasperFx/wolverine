using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TestingSupport;
using TestingSupport.Compliance;
using Wolverine.MQTT.Internals;
using Wolverine.Runtime;

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

            opts.ListenToMqttTopic(senderTopic).RetainMessages();

            opts.PublishAllMessages().ToMqttTopic(receiverTopic).RetainMessages().SendInline();
        });

        await ReceiverIs(opts =>
        {
            opts.UseMqttWithLocalBroker(port);

            opts.ListenToMqttTopic(receiverTopic).Named("receiver").RetainMessages().ProcessInline();
        });
    }

    public LocalMqttBroker Broker { get; private set; }

    public async Task DisposeAsync()
    {
        await Broker.StopAsync();
        await Broker.DisposeAsync();
        await DisposeAsync();
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
        
        transport.ResponseTopic.ShouldBe("wolverine/response/" + options.Durability.AssignedNodeNumber);
        
        transport.ReplyEndpoint().ShouldBeOfType<MqttTopic>().TopicName.ShouldBe(transport.ResponseTopic);
    }

}