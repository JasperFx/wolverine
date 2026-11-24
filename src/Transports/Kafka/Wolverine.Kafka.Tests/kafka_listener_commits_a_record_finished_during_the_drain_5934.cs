using System.Collections.Concurrent;
using Dekaf.Consumer;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;

namespace Wolverine.Kafka.Tests;

// GH-5934. A graceful stop drains the receiver, so a handler still running when the stop starts gets to finish. The
// Kafka listener used to close its consumer before that drain, so the record that handler finished could not be
// committed any more and the next consumer in the group handled it a second time. The consumer now stays open, with
// its partitions paused, until the drain is over.
//
// buffered_listener_redelivers_unfinished_messages covers the other half: a handler still running when the drain
// times out leaves its record to the next consumer.
public class kafka_listener_commits_a_record_finished_during_the_drain_5934
{
    private readonly BrokerName brokerName = new("drain5934");

    private Task<IHost> publisherAsync(string topicName) => WolverineHost.ForAsync(opts =>
    {
        opts.AddNamedKafkaBroker(brokerName, KafkaContainerFixture.ConnectionString).AutoProvision();
        opts.PublishAllMessages().ToKafkaTopicOnNamedBroker(brokerName, topicName).SendInline();
    });

    private Task<IHost> receiverAsync(string topicName, string groupId, ListenerShape shape) => WolverineHost.ForAsync(opts =>
    {
        // Plenty of room for the held handler to finish inside the drain
        opts.Durability.DrainTimeout = 20.Seconds();

        opts.AddNamedKafkaBroker(brokerName, KafkaContainerFixture.ConnectionString).AutoProvision();
        var listener = opts.ListenToKafkaTopicOnNamedBroker(brokerName, topicName)
            .ConfigureConsumer(x =>
            {
                x.GroupId = groupId;
                x.AutoOffsetReset = AutoOffsetReset.Earliest;
            });

        switch (shape)
        {
            case ListenerShape.Buffered:
                // Default CommitMode (StoreThenAutoFlush)
                listener.BufferedInMemory();
                break;

            case ListenerShape.BufferedPerMessage:
                listener.BufferedInMemory().CommitOffsets(CommitMode.PerMessage);
                break;

            case ListenerShape.Inline:
                listener.ProcessInline();
                break;
        }

        opts.Discovery.DisableConventionalDiscovery().IncludeType<DrainCommitHandler>();
    });

    public enum ListenerShape
    {
        Buffered,
        BufferedPerMessage,
        Inline
    }

    [Theory]
    [InlineData(ListenerShape.Buffered)]
    [InlineData(ListenerShape.BufferedPerMessage)]
    [InlineData(ListenerShape.Inline)]
    public async Task a_record_finished_during_the_drain_is_not_redelivered(ListenerShape shape)
    {
        DrainCommitState.Reset();

        var topic = Guid.NewGuid().ToString();
        var groupId = Guid.NewGuid().ToString();

        using var publisher = await publisherAsync(topic);

        // --- Phase 1: "held" is in its handler when the graceful stop starts, and finishes during the drain ---
        var firstBatch = new ConcurrentBag<string>();
        DrainCommitState.Sink = firstBatch;

        var first = await receiverAsync(topic, groupId, shape);

        for (var i = 0; i < 3; i++)
        {
            await publisher.SendAsync(new DrainCommitMessage { Id = "done-" + i });
        }

        await publisher.SendAsync(new DrainCommitMessage { Id = "held" });

        await waitForAsync(() => firstBatch.Count(x => x.StartsWith("done-")) == 3);
        await DrainCommitState.HeldStarted.Task.WaitAsync(30.Seconds(), TestContext.Current.CancellationToken);

        var stopping = first.StopAsync(CancellationToken.None);

        // Let the stop get past the listener and into the drain before the handler finishes
        await Task.Delay(2.Seconds(), TestContext.Current.CancellationToken);
        stopping.IsCompleted.ShouldBeFalse("the drain should still be waiting on the held handler");

        DrainCommitState.ReleaseHeld();

        await stopping.WaitAsync(60.Seconds(), TestContext.Current.CancellationToken);
        first.Dispose();

        firstBatch.ShouldContain("held");

        // --- Phase 2: the next consumer in the group starts after "held", not at it ---
        var secondBatch = new ConcurrentBag<string>();
        DrainCommitState.Sink = secondBatch;

        using var second = await receiverAsync(topic, groupId, shape);
        await publisher.SendAsync(new DrainCommitMessage { Id = "fresh" });

        await waitForAsync(() => secondBatch.Contains("fresh"),
            describe: () => $"second: [{string.Join(", ", secondBatch.Order())}]");

        // "fresh" comes after "held" in the partition, so a redelivered "held" would have shown up by now
        await Task.Delay(3.Seconds(), TestContext.Current.CancellationToken);

        secondBatch.ShouldNotContain("held",
            $"first: [{string.Join(", ", firstBatch.Order())}], second: [{string.Join(", ", secondBatch.Order())}]");
    }

    private static async Task waitForAsync(Func<bool> condition, int timeoutMs = 60000, Func<string>? describe = null)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < cutoff)
        {
            if (condition()) return;
            await Task.Delay(100);
        }

        throw new TimeoutException($"The condition was not met within {timeoutMs}ms. {describe?.Invoke()}");
    }
}

public class DrainCommitMessage
{
    public string Id { get; set; } = string.Empty;
}

public static class DrainCommitState
{
    public static ConcurrentBag<string>? Sink;

    public static TaskCompletionSource HeldStarted { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static Task WaitAtGateAsync() => Gate.Task;

    public static void ReleaseHeld() => Gate.TrySetResult();

    // One run per test case
    public static void Reset()
    {
        ReleaseHeld();
        HeldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public class DrainCommitHandler
{
    public static async Task Handle(DrainCommitMessage message)
    {
        if (message.Id == "held")
        {
            // Only the first time: a redelivery to the second consumer runs straight through
            if (DrainCommitState.HeldStarted.TrySetResult())
            {
                await DrainCommitState.WaitAtGateAsync();
            }
        }

        DrainCommitState.Sink?.Add(message.Id);
    }
}
