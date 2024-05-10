using Microsoft.Extensions.DependencyInjection;
using Oakton.Resources;
using Shouldly;
using TestingSupport;
using TestingSupport.Compliance;
using Wolverine.Runtime;

namespace Wolverine.Kafka.Tests;


public class BufferedComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    public BufferedComplianceFixture() : base(new Uri("kafka://topic/receiver"), 120)
    {
    }

    public async Task InitializeAsync()
    {
        var receiverTopic = "buffered.receiver";
        var senderTopic = "buffered.sender";

        OutboundAddress = new Uri("kafka://topic/" + receiverTopic);

        await SenderIs(opts =>
        {
            opts.UseKafka("localhost:29092").ConfigureConsumers(x => x.EnableAutoCommit = false);

            opts.ListenToKafkaTopic(senderTopic);

            opts.PublishAllMessages().ToKafkaTopic(receiverTopic).BufferedInMemory();

            opts.Services.AddResourceSetupOnStartup();
        });

        await ReceiverIs(opts =>
        {
            opts.UseKafka("localhost:29092");

            opts.ListenToKafkaTopic(receiverTopic).Named("receiver").BufferedInMemory();

            opts.Services.AddResourceSetupOnStartup();
        });
    }

    public new Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
}

[Collection("acceptance")]
public class BufferedSendingAndReceivingCompliance : TransportCompliance<BufferedComplianceFixture>
{
}

public class InlineComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{

    public static int Number = 0;

    public InlineComplianceFixture() : base(new Uri("kafka://topic/receiver"), 120)
    {

    }

    public async Task InitializeAsync()
    {
        var receiverTopic = "receiver.inline";
        var senderTopic = "sender.inline";

        OutboundAddress = new Uri("kafka://topic/" + receiverTopic);

        await SenderIs(opts =>
        {
            opts.UseKafka("localhost:29092").AutoProvision();

            opts.ListenToKafkaTopic(senderTopic).UseForReplies();

            opts.PublishAllMessages().ToKafkaTopic(receiverTopic).SendInline();

            opts.Services.AddResourceSetupOnStartup();
        });

        await ReceiverIs(opts =>
        {
            opts.UseKafka("localhost:29092").AutoProvision();

            opts.ListenToKafkaTopic(receiverTopic).Named("receiver").ProcessInline();

            opts.Services.AddResourceSetupOnStartup();
        });
    }

    public new Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
}

[Collection("acceptance")]
public class InlineSendingAndReceivingCompliance : TransportCompliance<InlineComplianceFixture>
{

}