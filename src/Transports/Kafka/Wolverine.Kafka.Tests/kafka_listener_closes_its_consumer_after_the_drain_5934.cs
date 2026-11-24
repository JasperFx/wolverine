using Dekaf;
using Dekaf.Consumer;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Kafka.Internals;
using Wolverine.Transports;

namespace Wolverine.Kafka.Tests;

/// <summary>
/// GH-5934. On a graceful stop, ListeningAgent stops the listener, drains the receiver, and only then disposes the
/// listener. KafkaListener.StopAsync used to flush and close the consumer right there, before the drain, so a handler
/// that finished during the drain had no consumer left to commit through and its record went to the next consumer in
/// the group. StopAsync now only stops fetching: every assigned partition is paused and the consume loop keeps
/// committing what finishes. DisposeAsync flushes and closes. No broker here: a scripted consumer stands in.
/// kafka_listener_commits_a_record_finished_during_the_drain_5934 proves it end to end against a broker.
/// </summary>
public class kafka_listener_closes_its_consumer_after_the_drain_5934
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly TopicPartition Partition0 = new("orders", 0);
    private static readonly TopicPartition Partition1 = new("orders", 1);

    private readonly IKafkaConsumer<string, byte[]> theConsumer = Substitute.For<IKafkaConsumer<string, byte[]>>();
    private readonly IReceiver theReceiver = Substitute.For<IReceiver>();

    public kafka_listener_closes_its_consumer_after_the_drain_5934()
    {
        // A quiet topic: every poll waits out its timeout, so the loop keeps passing
        theConsumer.ConsumeOneAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ci => quietPollAsync(ci.ArgAt<TimeSpan>(0), ci.ArgAt<CancellationToken>(1)));

        theConsumer.Assignment.Returns(new HashSet<TopicPartition> { Partition0, Partition1 });
    }

    private static async ValueTask<ConsumeResult<string, byte[]>?> quietPollAsync(TimeSpan timeout, CancellationToken token)
    {
        await Task.Delay(timeout < TimeSpan.FromMilliseconds(50) ? timeout : TimeSpan.FromMilliseconds(50), token);
        return null;
    }

    private static KafkaTopic topic(EndpointMode mode, CommitMode commitMode)
    {
        var topic = new KafkaTransport().Topics["orders"];
        topic.EnvelopeMapper = new KafkaEnvelopeMapper(topic);
        topic.Mode = mode;
        topic.CommitMode = commitMode;

        // Never reached by count, so only the flush commits
        topic.CommitBatchCount = 10_000;
        return topic;
    }

    private async Task<KafkaListener> listenAsync(KafkaTopic topic)
    {
        var listener = new KafkaListener(topic, new KafkaConsumerOptions { GroupId = "orders-group" }, theConsumer,
            [topic.TopicName], theReceiver, NullLogger<KafkaListener>.Instance, TimeSpan.FromSeconds(5));

        // Wait until the loop has initialized the consumer and is polling, as it is long before any stop in production
        await waitForAsync(() => theConsumer.ReceivedCalls().Any(x => x.GetMethodInfo().Name == nameof(IKafkaConsumer<string, byte[]>.ConsumeOneAsync)));

        return listener;
    }

    [Theory]
    [InlineData(EndpointMode.BufferedInMemory)]
    [InlineData(EndpointMode.Durable)]
    [InlineData(EndpointMode.Inline)]
    public async Task stop_pauses_every_assigned_partition_and_keeps_the_consumer_open(EndpointMode mode)
    {
        var paused = new TaskCompletionSource<TopicPartition[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        theConsumer.Partitions.When(x => x.Pause(Arg.Any<TopicPartition[]>()))
            .Do(ci => paused.TrySetResult(ci.Arg<TopicPartition[]>()));

        var listener = await listenAsync(topic(mode, CommitMode.PerMessage));

        await listener.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token);

        (await paused.Task.WaitAsync(TimeSpan.FromSeconds(10), Token)).ShouldBe([Partition0, Partition1], ignoreOrder: true);

        // Still in the group, so the receiver's drain can still commit through it
        await theConsumer.DidNotReceive().CloseAsync(Arg.Any<CancellationToken>());
        await theConsumer.DidNotReceive().DisposeAsync();

        await listener.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), Token);

        await theConsumer.Received().CloseAsync(Arg.Any<CancellationToken>());
        await theConsumer.Received().DisposeAsync();
    }

    [Fact]
    public async Task a_record_finished_after_the_stop_is_committed_by_the_consume_loop()
    {
        var committed = new TaskCompletionSource<TopicPartitionOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        theConsumer.When(x => x.CommitAsync(Arg.Any<IEnumerable<TopicPartitionOffset>>(), Arg.Any<CancellationToken>()))
            .Do(ci => committed.TrySetResult(ci.Arg<IEnumerable<TopicPartitionOffset>>().Single()));

        // PerMessage, so a finished record is committed by the loop's next pass
        var listener = await listenAsync(topic(EndpointMode.BufferedInMemory, CommitMode.PerMessage));

        // Delivered before the stop, still running when it comes
        var envelope = new Envelope { TopicName = "orders", PartitionId = 0, Offset = 7 };
        trackDelivered(listener, envelope);

        await listener.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token);

        // ... and finishes while the receiver drains
        await listener.CompleteAsync(envelope);

        (await committed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token))
            .ShouldBe(new TopicPartitionOffset("orders", 0, 8));

        await theConsumer.DidNotReceive().CloseAsync(Arg.Any<CancellationToken>());

        await listener.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), Token);
        await theConsumer.Received().CloseAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task a_record_finished_after_the_stop_is_flushed_before_the_close()
    {
        // Batched by count, never reached, so only the flush on dispose commits it
        var listener = await listenAsync(topic(EndpointMode.BufferedInMemory, CommitMode.BatchCount));

        var envelope = new Envelope { TopicName = "orders", PartitionId = 1, Offset = 41 };
        trackDelivered(listener, envelope);

        await listener.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token);
        await listener.CompleteAsync(envelope);

        await listener.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), Token);

        Received.InOrder(() =>
        {
            _ = theConsumer.CommitAsync(
                Arg.Is<IEnumerable<TopicPartitionOffset>>(x => x.Single() == new TopicPartitionOffset("orders", 1, 42)),
                Arg.Any<CancellationToken>());
            _ = theConsumer.CloseAsync(Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task a_record_still_unfinished_when_the_listener_is_disposed_is_never_committed()
    {
        var listener = await listenAsync(topic(EndpointMode.BufferedInMemory, CommitMode.BatchCount));

        // 7 is still running when the drain times out; 8 finished. Neither offset may be committed past 7
        var unfinished = new Envelope { TopicName = "orders", PartitionId = 0, Offset = 7 };
        var finished = new Envelope { TopicName = "orders", PartitionId = 0, Offset = 8 };
        trackDelivered(listener, unfinished);
        trackDelivered(listener, finished);

        await listener.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token);
        await listener.CompleteAsync(finished);
        await listener.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), Token);

        await theConsumer.DidNotReceive().CommitAsync(Arg.Any<IEnumerable<TopicPartitionOffset>>(), Arg.Any<CancellationToken>());

        // The consumer is closed anyway, so the group hands record 7 to the next consumer straight away
        await theConsumer.Received().CloseAsync(Arg.Any<CancellationToken>());
        await theConsumer.Received().DisposeAsync();
    }

    [Fact]
    public async Task nothing_is_handed_to_the_receiver_once_the_stop_returns()
    {
        var listener = await listenAsync(topic(EndpointMode.BufferedInMemory, CommitMode.PerMessage));
        await listener.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token);

        // Even if the broker hands back a record after the stop, it is left for the next consumer rather than handled
        theConsumer.ConsumeOneAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<ConsumeResult<string, byte[]>?>(
                TestRecords.Incoming("orders", "key", [1, 2, 3], partition: 0, offset: 99)));

        await Task.Delay(250, Token);

        await theReceiver.DidNotReceiveWithAnyArgs().ReceivedAsync(default!, default(Envelope)!);

        await listener.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), Token);
        await theConsumer.DidNotReceive().CommitAsync(Arg.Any<IEnumerable<TopicPartitionOffset>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task a_deferral_after_the_stop_is_left_for_the_next_consumer()
    {
        var listener = await listenAsync(topic(EndpointMode.BufferedInMemory, CommitMode.PerMessage));
        await listener.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token);

        // A latched receiver defers what it still had queued. Kafka can't hand it back, so it is simply not completed
        await listener.DeferAsync(new Envelope { TopicName = "orders", PartitionId = 0, Offset = 3 });
        await Task.Delay(KafkaDeferredRedelivery.Delay + TimeSpan.FromMilliseconds(250), Token);

        await theReceiver.DidNotReceiveWithAnyArgs().ReceivedAsync(default!, default(Envelope)!);

        await listener.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), Token);
    }

    // What the consume loop does for every record it hands the receiver
    private static void trackDelivered(KafkaListener listener, Envelope envelope)
    {
        listener.Committer.Track(envelope.TopicName!, envelope.PartitionId!.Value, envelope.Offset);
    }

    private static async Task waitForAsync(Func<bool> condition)
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < cutoff)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        throw new TimeoutException("The consume loop never started polling");
    }
}
