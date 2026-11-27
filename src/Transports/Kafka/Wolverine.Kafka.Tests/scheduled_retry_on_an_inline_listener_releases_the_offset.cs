using System.Collections.Concurrent;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;
using Xunit;

namespace Wolverine.Kafka.Tests;

/// <summary>
/// #5980. An inline Kafka listener cannot schedule a retry natively, so a failed attempt that schedules a retry writes
/// it to the durable inbox. The record's offset used to stay in flight after that, which pinned the committed position
/// of the partition at that record: nothing after it was committed either, and a restart or rebalance redelivered the
/// record and every record behind it. The offset is completed once the retry is in the inbox, so the committed position
/// moves past it.
/// </summary>
public class scheduled_retry_on_an_inline_listener_releases_the_offset
{
    private readonly BrokerName brokerName = new("inlineretryoffset");

    [Fact]
    public async Task the_committed_offset_moves_past_a_record_whose_retry_is_in_the_inbox()
    {
        var runId = Guid.NewGuid().ToString("N");
        var topic = $"inline-retry-{runId}";
        var groupId = $"inline-retry-{runId}";

        using var receiver = await WolverineHost.ForAsync(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();

            opts.AddNamedKafkaBroker(brokerName, KafkaContainerFixture.ConnectionString).AutoProvision();
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "kafka_inline_retry");

            opts.ListenToKafkaTopicOnNamedBroker(brokerName, topic)
                .ConfigureConsumer(x =>
                {
                    x.GroupId = groupId;
                    x.AutoOffsetReset = Dekaf.Consumer.AutoOffsetReset.Earliest;
                })
                .ProcessInline();

            opts.OnException<FailOnceException>().ScheduleRetry(250.Milliseconds());

            opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(InlineRetryRecordHandler));
            opts.Services.AddResourceSetupOnStartup();
        });

        using var publisher = await WolverineHost.ForAsync(opts =>
        {
            opts.AddNamedKafkaBroker(brokerName, KafkaContainerFixture.ConnectionString).AutoProvision();
            opts.PublishAllMessages().ToKafkaTopicOnNamedBroker(brokerName, topic).SendInline();
        });

        // The first record fails once and is retried from the inbox; the second is handled straight away
        await publisher.MessageBus().PublishAsync(new InlineRetryRecord(runId, 1, FailFirst: true));
        await publisher.MessageBus().PublishAsync(new InlineRetryRecord(runId, 2, FailFirst: false));

        await InlineRetryRecordHandler.WaitForSuccessAsync(runId, 1);
        await InlineRetryRecordHandler.WaitForSuccessAsync(runId, 2);

        // Both records are done, so the group's committed position has to move past both of them
        await committedOffsetShouldReachAsync(topic, groupId, 2);

        InlineRetryRecordHandler.AttemptsOf(runId, 1).ShouldBe(2);
        InlineRetryRecordHandler.AttemptsOf(runId, 2).ShouldBe(1);
    }

    private static async Task committedOffsetShouldReachAsync(string topic, string groupId, long offset)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = KafkaContainerFixture.ConnectionString
        }).Build();

        var partitions = admin.GetMetadata(topic, 10.Seconds()).Topics.Single().Partitions
            .Select(x => new TopicPartition(topic, new Partition(x.PartitionId)))
            .ToList();

        long committed = -1;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await admin.ListConsumerGroupOffsetsAsync(
                [new ConsumerGroupTopicPartitions(groupId, partitions)]);

            // Offset.Unset (nothing committed) is negative
            committed = result.Single().Partitions.Sum(x => Math.Max(0, x.Offset.Value));
            if (committed >= offset) return;

            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        committed.ShouldBeGreaterThanOrEqualTo(offset, "The committed offset never moved past the retried record");
    }
}

public record InlineRetryRecord(string RunId, int Sequence, bool FailFirst);

public class FailOnceException() : Exception("The first attempt fails");

public static class InlineRetryRecordHandler
{
    private static readonly ConcurrentDictionary<(string, int), int> _attempts = new();
    private static readonly ConcurrentDictionary<(string, int), TaskCompletionSource> _succeeded = new();

    public static void Handle(InlineRetryRecord record)
    {
        var key = (record.RunId, record.Sequence);
        var attempt = _attempts.AddOrUpdate(key, 1, (_, x) => x + 1);

        if (record.FailFirst && attempt == 1) throw new FailOnceException();

        completionFor(key).TrySetResult();
    }

    public static int AttemptsOf(string runId, int sequence) => _attempts.GetValueOrDefault((runId, sequence));

    public static Task WaitForSuccessAsync(string runId, int sequence)
    {
        return completionFor((runId, sequence)).Task.WaitAsync(60.Seconds());
    }

    private static TaskCompletionSource completionFor((string, int) key)
    {
        return _succeeded.GetOrAdd(key, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}
