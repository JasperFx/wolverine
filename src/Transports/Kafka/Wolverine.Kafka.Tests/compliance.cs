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
        ExpectsTransportHealthCheck = true;
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

        // GH-5676: see KafkaComplianceTopics.CreateAsync
        await KafkaComplianceTopics.CreateAsync(receiverTopic, senderTopic);

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
                x.OffsetCommitMode = OffsetCommitMode.Manual;
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
        ExpectsTransportHealthCheck = true;
    }

    public async ValueTask InitializeAsync()
    {
        var receiverTopic = _receiverTopic;
        var senderTopic = _senderTopic;

        OutboundAddress = new Uri("kafka://topic/" + receiverTopic);

        // GH-5676: see KafkaComplianceTopics.CreateAsync
        await KafkaComplianceTopics.CreateAsync(receiverTopic, senderTopic);

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

    /// <summary>
    /// GH-5676. Create a fixture's topics before either host starts. Kafka 4's consumer group protocol (KIP-848)
    /// assigns partitions in heartbeat responses, and the group coordinator picks up a new topic's metadata a moment
    /// after the topic is created. A listener that subscribed in that moment -- the topic created by its own host's
    /// resource setup, the subscription made about 60ms later -- was answered with no partitions and only got its
    /// partition on the next heartbeat, five seconds (the broker's group.consumer.heartbeat.interval.ms) later. That
    /// is the whole of can_send_and_wait's reply timeout (DefaultRemoteInvocationTimeout, 5s), so the request/reply
    /// failed with only "Sent" in its activity. The fixtures with fixed topic names never hit this: their topics
    /// already exist after the first run
    /// </summary>
    public static async Task CreateAsync(params string[] topics)
    {
        using var admin = new Confluent.Kafka.AdminClientBuilder(
            new Confluent.Kafka.AdminClientConfig { BootstrapServers = KafkaContainerFixture.ConnectionString }).Build();

        await admin.CreateTopicsAsync(topics.Select(x => new Confluent.Kafka.Admin.TopicSpecification
        {
            Name = x, NumPartitions = 1, ReplicationFactor = 1
        }));
    }

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
