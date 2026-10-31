using System.Collections.Concurrent;
using Dekaf;
using Dekaf.Consumer;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Kafka.Internals;
using Wolverine.Transports;

namespace Wolverine.Kafka.Tests;

// The listener's consume loop over a scripted consumer, no broker
public class kafka_listener_consume_loop
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly IKafkaConsumer<string, byte[]> theConsumer = Substitute.For<IKafkaConsumer<string, byte[]>>();

    // What a poll that waits returns, in order. Once empty, the poll waits out its timeout like a quiet topic
    private readonly ConcurrentQueue<Func<ConsumeResult<string, byte[]>>> _polls = new();

    // What a zero-timeout poll returns, in order, null meaning nothing fetched. A thrown exception is a failed poll
    private readonly ConcurrentQueue<Func<ConsumeResult<string, byte[]>?>> _drains = new();

    public kafka_listener_consume_loop()
    {
        theConsumer.ConsumeOneAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ci => next(ci.ArgAt<TimeSpan>(0), ci.ArgAt<CancellationToken>(1)));
    }

    private async ValueTask<ConsumeResult<string, byte[]>?> next(TimeSpan timeout, CancellationToken token)
    {
        if (timeout == TimeSpan.Zero)
        {
            return _drains.TryDequeue(out var drain) ? drain() : null;
        }

        if (_polls.TryDequeue(out var poll)) return poll();

        await Task.Delay(timeout, token);
        return null;
    }

    private static ConsumeResult<string, byte[]> record(long offset, int partition = 0, DateTimeOffset? timestamp = null)
    {
        return TestRecords.Incoming("orders", "key", "{}"u8.ToArray(), partition: partition, offset: offset,
            timestamp: timestamp);
    }

    private static KafkaTopic topic(Action<KafkaTopic>? configure = null)
    {
        var topic = new KafkaTransport().Topics["orders"];
        topic.EnvelopeMapper = new KafkaEnvelopeMapper(topic);
        configure?.Invoke(topic);
        return topic;
    }

    private KafkaListener listen(KafkaTopic topic, IReceiver receiver)
    {
        return new KafkaListener(topic, new KafkaConsumerOptions { GroupId = "orders-group" }, theConsumer,
            [topic.TopicName], receiver, NullLogger<KafkaListener>.Instance, TimeSpan.FromSeconds(5));
    }

    private TaskCompletionSource<TopicPartitionOffset> captureCommit()
    {
        var committed = new TaskCompletionSource<TopicPartitionOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        theConsumer.When(x => x.CommitAsync(Arg.Any<IEnumerable<TopicPartitionOffset>>(), Arg.Any<CancellationToken>()))
            .Do(ci => committed.TrySetResult(ci.Arg<IEnumerable<TopicPartitionOffset>>().Single()));
        return committed;
    }

    [Fact]
    public async Task a_failed_poll_mid_batch_still_dispatches_the_records_already_taken()
    {
        var batches = new TaskCompletionSource<Envelope[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = Substitute.For<IReceiver>();
        receiver.ReceivedAsync(Arg.Any<IListener>(), Arg.Any<Envelope[]>()).Returns(ci =>
        {
            batches.TrySetResult(ci.Arg<Envelope[]>());
            return ValueTask.CompletedTask;
        });

        _polls.Enqueue(() => record(0));
        _drains.Enqueue(() => record(1));
        _drains.Enqueue(() => throw new InvalidOperationException("fetch failed"));

        await using var listener = listen(topic(x => x.Mode = EndpointMode.Durable), receiver);

        var batch = await batches.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        batch.Select(x => x.Offset).ShouldBe([0L, 1L]);
    }

    [Fact]
    public async Task a_record_the_receiver_rejects_is_skipped_rather_than_holding_its_partition()
    {
        var receiver = Substitute.For<IReceiver>();
        receiver.ReceivedAsync(Arg.Any<IListener>(), Arg.Any<Envelope>())
            .Returns(_ => ValueTask.FromException(new InvalidOperationException("rejected")));
        var committed = captureCommit();

        _polls.Enqueue(() => record(5));

        await using var listener = listen(topic(), receiver);

        // The next pass commits past the rejected record
        var offset = await committed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        offset.Offset.ShouldBe(6);
    }

    [Fact]
    public async Task a_retry_tier_record_that_is_not_due_pauses_only_its_own_partition()
    {
        var receiver = Substitute.For<IReceiver>();

        // The rewind comes last, so once it is seen the partition has been paused too
        var rewound = new TaskCompletionSource<TopicPartitionOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        theConsumer.Positions.When(x => x.Seek(Arg.Any<TopicPartitionOffset>()))
            .Do(ci => rewound.TrySetResult(ci.Arg<TopicPartitionOffset>()));

        _polls.Enqueue(() => record(3, partition: 2));

        await using var listener = listen(topic(x => x.RetryTierDelay = TimeSpan.FromHours(1)), receiver);

        // Rewound to the record so it is delivered again once the partition resumes
        var offset = await rewound.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        offset.ShouldBe(new TopicPartitionOffset("orders", 2, 3));

        theConsumer.Partitions.Received(1).Pause(Arg.Is<TopicPartition[]>(x => x.Single() == new TopicPartition("orders", 2)));
        await receiver.DidNotReceive().ReceivedAsync(Arg.Any<IListener>(), Arg.Any<Envelope>());
    }

    [Fact]
    public async Task a_paused_retry_tier_partition_resumes_and_dispatches_once_its_record_is_due()
    {
        var received = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = Substitute.For<IReceiver>();
        receiver.ReceivedAsync(Arg.Any<IListener>(), Arg.Any<Envelope>()).Returns(ci =>
        {
            received.TrySetResult(ci.Arg<Envelope>());
            return ValueTask.CompletedTask;
        });

        var produced = DateTimeOffset.UtcNow;

        // The record comes back once the partition is resumed
        theConsumer.Partitions.When(x => x.Resume(Arg.Any<TopicPartition[]>()))
            .Do(_ => _polls.Enqueue(() => record(3, timestamp: produced)));
        _polls.Enqueue(() => record(3, timestamp: produced));

        await using var listener = listen(topic(x => x.RetryTierDelay = TimeSpan.FromMilliseconds(500)), receiver);

        var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        envelope.Offset.ShouldBe(3);
        DateTimeOffset.UtcNow.ShouldBeGreaterThanOrEqualTo(produced.AddMilliseconds(500));
    }
}
