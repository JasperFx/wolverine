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

    public async ValueTask InitializeAsync()
    {
        var receiverTopic = "buffered.dlq.receiver";
        var senderTopic = "buffered.dlq.sender";

        OutboundAddress = new Uri("kafka://topic/" + receiverTopic);

        await ReceiverIs(opts =>
        {
            // Dead-letter onto this fixture's own topic rather than the shared default: suites like
            // DeadLetterQueueTests and kafka_retry_topics read the first message off their DLQ topic,
            // and this fixture's compliance failures landing on the shared default poisoned them.
            opts.UseKafka(KafkaContainerFixture.ConnectionString)
                .AutoProvision()
                .DeadLetterQueueTopicName("buffered.dlq.dead-letter");

            opts.ListenToKafkaTopic(receiverTopic)
                .Named("receiver")
                .BufferedInMemory()
                .EnableNativeDeadLetterQueue();

            opts.Services.AddResourceSetupOnStartup();
        });

        await SenderIs(opts =>
        {
            opts.UseKafka(KafkaContainerFixture.ConnectionString)
                .AutoProvision()
                .ConfigureConsumers(x => x.EnableAutoCommit = false);

            opts.ListenToKafkaTopic(senderTopic);

            opts.PublishAllMessages().ToKafkaTopic(receiverTopic).BufferedInMemory();

            opts.Services.AddResourceSetupOnStartup();
        });
    }
}

public class BufferedSendingAndReceivingWithDlqCompliance : TransportCompliance<BufferedComplianceWithDlqFixture>;
