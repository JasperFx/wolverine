using System.Collections.Concurrent;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;

namespace Wolverine.Kafka.Tests;

// GH-3150: end-to-end proof that the default StoreThenAutoFlush commit strategy actually commits the
// processed offsets — a second consumer in the same group resumes past them rather than reprocessing.
public class commit_strategy_end_to_end
{
    private readonly BrokerName brokerName = new("commit3150");

    private Task<IHost> publisherAsync(string topicName) => WolverineHost.ForAsync(opts =>
    {
        opts.AddNamedKafkaBroker(brokerName, KafkaContainerFixture.ConnectionString).AutoProvision();
        opts.PublishAllMessages().ToKafkaTopicOnNamedBroker(brokerName, topicName).SendInline();
    });

    private Task<IHost> receiverAsync(string topicName, string groupId) => WolverineHost.ForAsync(opts =>
    {
        opts.AddNamedKafkaBroker(brokerName, KafkaContainerFixture.ConnectionString).AutoProvision();
        opts.ListenToKafkaTopicOnNamedBroker(brokerName, topicName)
            .ProcessInline()
            // Default CommitMode (StoreThenAutoFlush) — not overridden.
            .ConfigureConsumer(x =>
            {
                x.GroupId = groupId;
                x.AutoOffsetReset = AutoOffsetReset.Earliest;
            });

        opts.Discovery.DisableConventionalDiscovery().IncludeType<CommitResumeHandler>();
    });

    [Fact]
    public async Task second_consumer_in_same_group_resumes_past_committed_offsets()
    {
        var topic = Guid.NewGuid().ToString();
        var groupId = Guid.NewGuid().ToString();

        using var publisher = await publisherAsync(topic);

        // --- Phase 1: first consumer processes the initial batch, then shuts down gracefully ---
        var firstBatch = new ConcurrentBag<string>();
        CommitResumeState.Sink = firstBatch;

        var first = await receiverAsync(topic, groupId);
        for (var i = 0; i < 5; i++)
        {
            await publisher.SendAsync(new CommitResumeMessage { Id = "first-" + i });
        }

        await waitForCountAsync(firstBatch, 5);

        // Graceful shutdown flushes/commits the stored offsets (StoreThenAutoFlush + clean Close).
        await first.StopAsync();
        first.Dispose();

        // --- Phase 2: a fresh consumer in the SAME group should only see the new messages ---
        var secondBatch = new ConcurrentBag<string>();
        CommitResumeState.Sink = secondBatch;

        using var second = await receiverAsync(topic, groupId);
        for (var i = 0; i < 3; i++)
        {
            await publisher.SendAsync(new CommitResumeMessage { Id = "second-" + i });
        }

        await waitForCountAsync(secondBatch, 3);

        // Give any (incorrect) redelivery of the first batch a chance to show up before asserting.
        await Task.Delay(1000);

        secondBatch.ShouldNotContain(x => x.StartsWith("first-"),
            "The second consumer reprocessed already-committed messages — offsets were not committed/flushed");
        secondBatch.Count(x => x.StartsWith("second-")).ShouldBe(3);
    }

    private static async Task waitForCountAsync(ConcurrentBag<string> bag, int count, int timeoutMs = 30000)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < cutoff)
        {
            if (bag.Count >= count) return;
            await Task.Delay(100);
        }

        throw new TimeoutException($"Only received {bag.Count} of {count} expected messages within {timeoutMs}ms");
    }
}

public class CommitResumeMessage
{
    public string Id { get; set; } = string.Empty;
}

public static class CommitResumeState
{
    public static ConcurrentBag<string>? Sink;
}

public class CommitResumeHandler
{
    public static void Handle(CommitResumeMessage message)
    {
        CommitResumeState.Sink?.Add(message.Id);
    }
}
