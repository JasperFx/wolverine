using Dekaf.Consumer;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Compliance;
using Xunit;
using Xunit.Sdk;

namespace Wolverine.Kafka.Tests;

public class BufferedComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    public BufferedComplianceFixture() : base(new Uri("kafka://topic/receiver"), 120)
    {
    }

    // GH-4982: unique per fixture instance, and TransportCompliance builds a fresh fixture for every test.
    // With fixed names, whatever an earlier test (or an earlier run against the same broker) left
    // uncommitted on the topic was consumed by the next test's brand new receiver.
    private readonly string _receiverTopic = KafkaComplianceTopics.Unique("buffered.receiver");
    private readonly string _senderTopic = KafkaComplianceTopics.Unique("buffered.sender");

    public async ValueTask InitializeAsync()
    {
        var receiverTopic = _receiverTopic;
        var senderTopic = _senderTopic;

        OutboundAddress = new Uri("kafka://topic/" + receiverTopic);


        await ReceiverIs(opts =>
        {
            // A brand new topic and consumer group per test, so read from the start of the topic. The default
            // of Latest loses any message the sender produces before the group finishes joining.
            opts.UseKafka(KafkaContainerFixture.ConnectionString)
                .ConfigureConsumers(x => x.AutoOffsetReset = AutoOffsetReset.Earliest);

            opts.ListenToKafkaTopic(receiverTopic).Named("receiver").BufferedInMemory();

            opts.Services.AddResourceSetupOnStartup();
        });
        
        await SenderIs(opts =>
        {
            opts.UseKafka(KafkaContainerFixture.ConnectionString).ConfigureConsumers(x =>
            {
                x.EnableAutoCommit = false;
                x.AutoOffsetReset = AutoOffsetReset.Earliest;
            });

            opts.ListenToKafkaTopic(senderTopic);

            opts.PublishAllMessages().ToKafkaTopic(receiverTopic).BufferedInMemory();

            opts.Services.AddResourceSetupOnStartup();
        });
    }

    protected override Task AfterDisposeAsync() => KafkaComplianceTopics.DeleteAsync(_receiverTopic, _senderTopic);
}

public class BufferedSendingAndReceivingCompliance : TransportCompliance<BufferedComplianceFixture>;

public class InlineComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    // GH-4982: see BufferedComplianceFixture. The sender's consumer group is unique for the same reason.
    private readonly string _receiverTopic = KafkaComplianceTopics.Unique("receiver.inline");
    private readonly string _senderTopic = KafkaComplianceTopics.Unique("sender.inline");

    public InlineComplianceFixture() : base(new Uri("kafka://topic/receiver"), 120)
    {
    }

    public async ValueTask InitializeAsync()
    {
        var receiverTopic = _receiverTopic;
        var senderTopic = _senderTopic;

        OutboundAddress = new Uri("kafka://topic/" + receiverTopic);

        await SenderIs(opts =>
        {
            opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();

            opts.ListenToKafkaTopic(senderTopic).UseForReplies().ConfigureConsumer(consumer =>
            {
                consumer.GroupId = senderTopic;
                consumer.AutoOffsetReset = AutoOffsetReset.Earliest;
            });

            opts.PublishAllMessages().ToKafkaTopic(receiverTopic).SendInline();

            opts.Services.AddResourceSetupOnStartup();
        });

        await ReceiverIs(opts =>
        {
            opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision()
                .ConfigureConsumers(x => x.AutoOffsetReset = AutoOffsetReset.Earliest);

            opts.ListenToKafkaTopic(receiverTopic).Named("receiver").ProcessInline();

            opts.Services.AddResourceSetupOnStartup();
        });
    }

    protected override Task AfterDisposeAsync() => KafkaComplianceTopics.DeleteAsync(_receiverTopic, _senderTopic);
}

public class InlineSendingAndReceivingCompliance : TransportCompliance<InlineComplianceFixture>;

internal static class KafkaComplianceTopics
{
    public static string Unique(string prefix) => $"{prefix}.{Guid.NewGuid():N}";

    public static async Task DeleteAsync(params string[] topics)
    {
        try
        {
            using var admin = new Confluent.Kafka.AdminClientBuilder(
                new Confluent.Kafka.AdminClientConfig { BootstrapServers = KafkaContainerFixture.ConnectionString }).Build();
            await admin.DeleteTopicsAsync(topics);
        }
        catch
        {
            // Best effort. A topic that was never created cannot leak into another test anyway
        }
    }
}
