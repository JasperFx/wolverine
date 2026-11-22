using System.Text;
using Confluent.Kafka;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ErrorHandling;
using Wolverine.Kafka.Internals;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace Wolverine.Kafka.Tests;

/// <summary>
/// GH-5803. #5772 keys a raw JSON copy of a received record (a dead-letter or retry-tier copy) by the key the
/// record arrived with, and tells a received record from an application send by <see cref="Envelope.PartitionId" />.
/// That is not persisted, so a record that went through the durable inbox came back looking like an application
/// send, and its copy took the consumer group name the listener stamps into <see cref="Envelope.GroupId" />. The
/// received key now travels with the envelope in a Wolverine-owned header that the inbox keeps.
/// </summary>
public class kafka_recovered_record_keeps_its_key_5803
{
    [Fact]
    public async Task a_copy_of_a_record_recovered_from_the_inbox_keeps_its_key()
    {
        var mapper = buildMapper();
        var envelope = recovered(received(mapper, "customer-7"));

        envelope.PartitionId.ShouldBeNull();
        envelope.GroupId.ShouldBe("my-consumer-group");

        var record = await mapper.CreateMessage(envelope);

        record.Key.ShouldBe("customer-7");
    }

    [Fact]
    public async Task a_copy_of_a_keyless_record_recovered_from_the_inbox_does_not_take_the_consumer_group_name()
    {
        var mapper = buildMapper();
        var envelope = recovered(received(mapper, null));

        var record = await mapper.CreateMessage(envelope);

        record.Key.ShouldBeNull();
    }

    [Fact]
    public async Task the_received_key_is_not_written_out_as_a_kafka_header()
    {
        var mapper = buildMapper();
        var envelope = recovered(received(mapper, "customer-7"));

        var record = await mapper.CreateMessage(envelope);

        (record.Headers?.Any(x => x.Key == JsonOnlyMapper.ReceivedRecordKeyHeader) ?? false).ShouldBeFalse();
    }

    [Fact]
    public async Task a_record_key_header_on_the_incoming_record_does_not_override_the_records_own_key()
    {
        var mapper = buildMapper();
        var message = new Message<string, byte[]>
        {
            Key = "customer-7",
            Value = Encoding.UTF8.GetBytes("{\"Color\":\"blue\"}"),
            Headers = new Headers { { JsonOnlyMapper.ReceivedRecordKeyHeader, Encoding.UTF8.GetBytes("forged") } }
        };

        var envelope = mapper.CreateEnvelope("recovered-5803", message);
        envelope.GroupId = "my-consumer-group";

        var record = await mapper.CreateMessage(recovered(envelope));

        record.Key.ShouldBe("customer-7");
    }

    [Fact]
    public async Task an_application_message_still_keeps_the_group_id_it_set()
    {
        var envelope = recovered(new Envelope
        {
            Data = Encoding.UTF8.GetBytes("{}"), GroupId = "group-1", PartitionKey = "pk-1"
        });

        var record = await buildMapper().CreateMessage(envelope);

        record.Key.ShouldBe("group-1");
    }

    // What KafkaListener.buildEnvelope does with a record off the broker, with the default consumer group stamping
    private static Envelope received(JsonOnlyMapper mapper, string? key)
    {
        var message = new Message<string, byte[]>
        {
            Key = key!,
            Value = Encoding.UTF8.GetBytes("{\"Color\":\"blue\"}")
        };
        var envelope = mapper.CreateEnvelope("recovered-5803", message);
        envelope.TopicName = "recovered-5803";
        envelope.Offset = 41;
        envelope.PartitionId = 2;
        envelope.GroupId = "my-consumer-group";

        return envelope;
    }

    // What the durable inbox stores and hands back on recovery
    private static Envelope recovered(Envelope envelope)
    {
        return EnvelopeSerializer.Deserialize(EnvelopeSerializer.Serialize(envelope));
    }

    private static JsonOnlyMapper buildMapper()
    {
        var topic = new KafkaTopic(new KafkaTransport(), "recovered-5803", Configuration.EndpointRole.Application)
        {
            MessageType = typeof(ColorMessage)
        };

        return new JsonOnlyMapper(topic);
    }
}

/// <summary>
/// GH-5803 end to end. A raw JSON record is recovered from the durable inbox (stored unowned, the way a crashed
/// node leaves it) and dead-lettered onto a Kafka dead letter topic that publishes raw JSON. The dead letter copy
/// keeps the record's key instead of taking the consumer group name.
/// </summary>
public class kafka_recovered_record_dead_letter_keeps_its_key_5803 : IAsyncLifetime
{
    private readonly string _topicName = $"recovered-key-{Guid.NewGuid():N}";
    private readonly string _dlqTopicName = $"recovered-key-dlq-{Guid.NewGuid():N}";
    private readonly string _consumerGroup = $"recovered-key-group-{Guid.NewGuid():N}";
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString)
                    .AutoProvision()
                    .DeadLetterQueueTopicName(_dlqTopicName);

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "kafka_5803");

                opts.ListenToKafkaTopic(_topicName)
                    .ExtendConsumerConfiguration(c => c.GroupId = _consumerGroup)
                    .UseDurableInbox()
                    .EnableNativeDeadLetterQueue()
                    .ReceiveRawJson<RecoveredRecordKeyMessage>();

                // The dead letter topic is raw JSON too, so its copies go through JsonOnlyMapper
                opts.PublishMessage<RecoveredRecordKeyDeadLetter>().ToKafkaTopic(_dlqTopicName).PublishRawJson();

                opts.Policies.OnException<RecoveredRecordKeyException>().MoveToErrorQueue();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<RecoveredRecordKeyMessageHandler>();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    [Fact]
    public async Task the_dead_letter_copy_keeps_the_record_key()
    {
        var runtime = _host.GetRuntime();
        var topic = runtime.Options.Transports.GetOrCreate<KafkaTransport>().Topics[_topicName];

        // What the listener stored in the inbox for this record, now owned by nobody as after a crash
        var message = new Message<string, byte[]>
        {
            Key = "customer-7",
            Value = Encoding.UTF8.GetBytes("{\"Name\":\"recovered\"}")
        };
        var envelope = topic.EnvelopeMapper!.CreateEnvelope(_topicName, message);
        envelope.TopicName = _topicName;
        envelope.Offset = 3;
        envelope.PartitionId = 0;
        envelope.GroupId = _consumerGroup;
        envelope.Destination = topic.Uri;
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.OwnerId = TransportConstants.AnyNode;

        await runtime.Storage.Inbox.StoreIncomingAsync(envelope);

        var deadLetter = consumeDeadLetter(90.Seconds());
        deadLetter.ShouldNotBeNull("Expected the recovered record on the dead letter topic");

        deadLetter.Message.Key.ShouldBe("customer-7");
    }

    private ConsumeResult<string, byte[]>? consumeDeadLetter(TimeSpan timeout)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = KafkaContainerFixture.ConnectionString,
            GroupId = $"recovered-key-verify-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true
        };

        using var consumer = new ConsumerBuilder<string, byte[]>(config).Build();
        consumer.Subscribe(_dlqTopicName);

        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var result = consumer.Consume(TimeSpan.FromSeconds(5));
                if (result != null) return result;
            }
            catch (ConsumeException)
            {
                // The topic may not exist yet
            }
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}

public record RecoveredRecordKeyMessage(string Name);

public record RecoveredRecordKeyDeadLetter(string Name);

public class RecoveredRecordKeyException() : Exception("Always fails");

public class RecoveredRecordKeyMessageHandler
{
    public void Handle(RecoveredRecordKeyMessage message) => throw new RecoveredRecordKeyException();
}
