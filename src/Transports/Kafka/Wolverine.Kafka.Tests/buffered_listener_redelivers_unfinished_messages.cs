using System.Collections.Concurrent;
using Dekaf.Consumer;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;

namespace Wolverine.Kafka.Tests;

// The Kafka docs say a BufferedInMemory listener with the default commit strategy (StoreThenAutoFlush) only stores
// an offset once its message finishes, so a node that goes down with a message still in flight gets it redelivered
// rather than losing it. This proves that end to end. The first consumer stops while one message is wedged in its
// handler, past a short DrainTimeout, which leaves that message unfinished exactly as a crash would. A second
// consumer in the same group must then receive it again, along with everything after it.
//
// GH-5926. A buffered receiver used to complete each record's offset as soon as it queued the record, so the committed
// offset passed "stuck" before it ever ran. The same scenario runs under PerMessage commits and for an inline listener,
// which shares the stop path.
public class buffered_listener_redelivers_unfinished_messages
{
    private readonly BrokerName brokerName = new("bufferedredelivery");

    private Task<IHost> publisherAsync(string topicName) => WolverineHost.ForAsync(opts =>
    {
        opts.AddNamedKafkaBroker(brokerName, KafkaContainerFixture.ConnectionString).AutoProvision();
        opts.PublishAllMessages().ToKafkaTopicOnNamedBroker(brokerName, topicName).SendInline();
    });

    private Task<IHost> receiverAsync(string topicName, string groupId, ListenerShape shape) => WolverineHost.ForAsync(opts =>
    {
        // Don't wait out the default 30 seconds for the wedged handler
        opts.Durability.DrainTimeout = 1.Seconds();

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
                // Default CommitMode (StoreThenAutoFlush), not overridden
                listener.BufferedInMemory();
                break;

            case ListenerShape.BufferedPerMessage:
                listener.BufferedInMemory().CommitOffsets(CommitMode.PerMessage);
                break;

            case ListenerShape.Inline:
                listener.ProcessInline();
                break;
        }

        opts.Discovery.DisableConventionalDiscovery().IncludeType<BufferedRedeliveryHandler>();
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
    public async Task an_unfinished_message_is_redelivered_to_the_next_consumer_in_the_group(ListenerShape shape)
    {
        BufferedRedeliveryState.Reset();

        var topic = Guid.NewGuid().ToString();
        var groupId = Guid.NewGuid().ToString();

        using var publisher = await publisherAsync(topic);

        try
        {
            // --- Phase 1: "stuck" never finishes on the first consumer ---
            var firstBatch = new ConcurrentBag<string>();
            BufferedRedeliveryState.Sink = firstBatch;
            BufferedRedeliveryState.BlockStuck = true;

            var first = await receiverAsync(topic, groupId, shape);

            for (var i = 0; i < 5; i++)
            {
                await publisher.SendAsync(new BufferedRedeliveryMessage { Id = "done-" + i });
            }

            await publisher.SendAsync(new BufferedRedeliveryMessage { Id = "stuck" });

            for (var i = 0; i < 3; i++)
            {
                await publisher.SendAsync(new BufferedRedeliveryMessage { Id = "after-" + i });
            }

            await waitForAsync(() => firstBatch.Count(x => x.StartsWith("done-")) == 5);
            await waitForAsync(() => BufferedRedeliveryState.StuckStarted.Task.IsCompleted);

            // Stand-in for a crash: "stuck" is still in its handler when the node goes away
            await first.StopAsync(TestContext.Current.CancellationToken);
            first.Dispose();

            firstBatch.ShouldNotContain("stuck");

            // --- Phase 2: a fresh consumer in the same group gets the unfinished work again ---
            var secondBatch = new ConcurrentBag<string>();
            BufferedRedeliveryState.Sink = secondBatch;
            BufferedRedeliveryState.BlockStuck = false;

            using var second = await receiverAsync(topic, groupId, shape);
            await publisher.SendAsync(new BufferedRedeliveryMessage { Id = "fresh" });

            await waitForAsync(() => secondBatch.Contains("fresh") && secondBatch.Contains("stuck")
                                     && secondBatch.Count(x => x.StartsWith("after-")) == 3,
                describe: () => $"first: [{string.Join(", ", firstBatch.Order())}], second: [{string.Join(", ", secondBatch.Order())}]");

            // The messages that finished before "stuck" were committed, so they don't come back
            secondBatch.ShouldNotContain(x => x.StartsWith("done-"));
        }
        finally
        {
            BufferedRedeliveryState.ReleaseStuck();
        }
    }

    private static async Task waitForAsync(Func<bool> condition, int timeoutMs = 30000, Func<string>? describe = null)
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

public class BufferedRedeliveryMessage
{
    public string Id { get; set; } = string.Empty;
}

public static class BufferedRedeliveryState
{
    public static ConcurrentBag<string>? Sink;
    public static volatile bool BlockStuck;

    public static TaskCompletionSource StuckStarted { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static Task WaitAtGateAsync() => Gate.Task;

    public static void ReleaseStuck() => Gate.TrySetResult();

    // One run per test case
    public static void Reset()
    {
        ReleaseStuck();
        StuckStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public class BufferedRedeliveryHandler
{
    public static async Task Handle(BufferedRedeliveryMessage message)
    {
        if (message.Id == "stuck" && BufferedRedeliveryState.BlockStuck)
        {
            BufferedRedeliveryState.StuckStarted.TrySetResult();

            // Wedged until the test is over, so this message never finishes on the first consumer
            await BufferedRedeliveryState.WaitAtGateAsync();
            return;
        }

        BufferedRedeliveryState.Sink?.Add(message.Id);
    }
}
