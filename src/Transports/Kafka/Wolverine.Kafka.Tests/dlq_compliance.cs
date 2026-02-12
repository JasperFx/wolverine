using Confluent.Kafka;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.ComplianceTests.Compliance;

namespace Wolverine.Kafka.Tests;

public class BufferedComplianceWithDlqFixture : TransportComplianceFixture, IAsyncLifetime
{
    public BufferedComplianceWithDlqFixture() : base(new Uri("kafka://topic/receiver"), 120)
    {
    }

    public async Task InitializeAsync()
    {
        var receiverTopic = "buffered.dlq.receiver";
        var senderTopic = "buffered.dlq.sender";

        OutboundAddress = new Uri("kafka://topic/" + receiverTopic);

        await ReceiverIs(opts =>
        {
            opts.UseKafka("localhost:9092").AutoProvision();

            opts.ListenToKafkaTopic(receiverTopic)
                .Named("receiver")
                .BufferedInMemory()
                .EnableNativeDeadLetterQueue();

            opts.Services.AddResourceSetupOnStartup();
        });

        await SenderIs(opts =>
        {
            opts.UseKafka("localhost:9092")
                .AutoProvision()
                .ConfigureConsumers(x => x.EnableAutoCommit = false);

            opts.ListenToKafkaTopic(senderTopic);

            opts.PublishAllMessages().ToKafkaTopic(receiverTopic).BufferedInMemory();

            opts.Services.AddResourceSetupOnStartup();
        });
    }

    public new Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
}

public class BufferedSendingAndReceivingWithDlqCompliance : TransportCompliance<BufferedComplianceWithDlqFixture>;
