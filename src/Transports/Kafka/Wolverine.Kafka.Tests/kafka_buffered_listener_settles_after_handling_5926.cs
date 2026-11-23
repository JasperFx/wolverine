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
/// GH-5926. A buffered Kafka listener completes each record's offset once the record finishes, not when the buffered
/// receiver queues it, so back pressure pauses its partitions instead of closing its consumer: closing it would hand
/// the backlog this node is still working through back to the group. No broker here: a scripted consumer stands in.
/// buffered_listener_redelivers_unfinished_messages and buffered_kafka_back_pressure_pauses_in_place_5926 prove the
/// end to end behaviour against a broker.
/// </summary>
public class kafka_buffered_listener_settles_after_handling_5926
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly TopicPartition Partition0 = new("orders", 0);
    private static readonly TopicPartition Partition1 = new("orders", 1);

    private readonly IKafkaConsumer<string, byte[]> theConsumer = Substitute.For<IKafkaConsumer<string, byte[]>>();

    public kafka_buffered_listener_settles_after_handling_5926()
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

    private static KafkaTopic topic(EndpointMode mode)
    {
        var topic = new KafkaTransport().Topics["orders"];
        topic.EnvelopeMapper = new KafkaEnvelopeMapper(topic);
        topic.Mode = mode;
        return topic;
    }

    private KafkaListener listen(KafkaTopic topic)
    {
        return new KafkaListener(topic, new KafkaConsumerOptions { GroupId = "orders-group" }, theConsumer,
            [topic.TopicName], Substitute.For<IReceiver>(), NullLogger<KafkaListener>.Instance, TimeSpan.FromSeconds(5));
    }

    private TaskCompletionSource<TopicPartition[]> capture(Action<IKafkaConsumer<string, byte[]>, Action<TopicPartition[]>> when)
    {
        var captured = new TaskCompletionSource<TopicPartition[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        when(theConsumer, x => captured.TrySetResult(x));
        return captured;
    }

    [Fact]
    public void the_kafka_listener_is_settled_after_handling()
    {
        typeof(KafkaListener).IsAssignableTo(typeof(ISettlesAfterHandlingListener)).ShouldBeTrue();
    }

    [Fact]
    public async Task a_buffered_listener_pauses_in_place_for_back_pressure()
    {
        await using var listener = listen(topic(EndpointMode.BufferedInMemory));
        ((IPausesForBackPressure)listener).PausesForBackPressure.ShouldBeTrue();
    }

    [Fact]
    public async Task a_durable_listener_is_still_stopped_for_back_pressure()
    {
        await using var listener = listen(topic(EndpointMode.Durable));
        ((IPausesForBackPressure)listener).PausesForBackPressure.ShouldBeFalse();
    }

    [Fact]
    public async Task back_pressure_pauses_every_assigned_partition_and_the_resume_resumes_them()
    {
        var paused = capture((c, done) =>
            c.Partitions.When(x => x.Pause(Arg.Any<TopicPartition[]>())).Do(ci => done(ci.Arg<TopicPartition[]>())));
        var resumed = capture((c, done) =>
            c.Partitions.When(x => x.Resume(Arg.Any<TopicPartition[]>())).Do(ci => done(ci.Arg<TopicPartition[]>())));

        await using var listener = listen(topic(EndpointMode.BufferedInMemory));
        var pausable = (IPausesForBackPressure)listener;

        await pausable.PauseForBackPressureAsync();
        listener.IsPausedForBackPressure.ShouldBeTrue();

        (await paused.Task.WaitAsync(TimeSpan.FromSeconds(10), Token)).ShouldBe([Partition0, Partition1], ignoreOrder: true);

        await pausable.ResumeAfterBackPressureAsync();

        (await resumed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token)).ShouldBe([Partition0, Partition1], ignoreOrder: true);

        // Paused and resumed in place: the consumer never left the group
        await theConsumer.DidNotReceive().CloseAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task a_partition_assigned_while_paused_is_paused_too()
    {
        var partition2 = new TopicPartition("orders", 2);
        var pausedPartition2 = capture((c, done) =>
            c.Partitions.When(x => x.Pause(Arg.Is<TopicPartition[]>(a => a.Contains(partition2))))
                .Do(ci => done(ci.Arg<TopicPartition[]>())));

        var firstPause = capture((c, done) =>
            c.Partitions.When(x => x.Pause(Arg.Is<TopicPartition[]>(a => a.Contains(Partition0))))
                .Do(ci => done(ci.Arg<TopicPartition[]>())));

        await using var listener = listen(topic(EndpointMode.BufferedInMemory));
        await ((IPausesForBackPressure)listener).PauseForBackPressureAsync();
        await firstPause.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        // A rebalance hands this consumer another partition while back pressure has it paused
        theConsumer.Assignment.Returns(new HashSet<TopicPartition> { Partition0, Partition1, partition2 });

        // Only the new partition is paused on the next pass
        (await pausedPartition2.Task.WaitAsync(TimeSpan.FromSeconds(10), Token)).ShouldBe([partition2]);
    }

    [Fact]
    public async Task a_partition_revoked_and_assigned_again_while_paused_is_paused_again()
    {
        var pauses = new List<TopicPartition[]>();
        var pausedAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        theConsumer.Partitions.When(x => x.Pause(Arg.Any<TopicPartition[]>())).Do(ci =>
        {
            lock (pauses)
            {
                pauses.Add(ci.Arg<TopicPartition[]>());
                firstPause.TrySetResult();
                if (pauses.Count(x => x.Contains(Partition1)) == 2) pausedAgain.TrySetResult();
            }
        });

        await using var listener = listen(topic(EndpointMode.BufferedInMemory));
        await ((IPausesForBackPressure)listener).PauseForBackPressureAsync();
        await firstPause.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        // A rebalance takes partition 1 away, and a later one hands it back
        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        theConsumer.Assignment.Returns(_ =>
        {
            revoked.TrySetResult();
            return new HashSet<TopicPartition> { Partition0 };
        });
        await revoked.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        // Wait for a whole pass with only partition 0 before handing partition 1 back
        var passes = 0;
        var sawRevokedPass = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        theConsumer.Assignment.Returns(_ =>
        {
            if (++passes >= 2) sawRevokedPass.TrySetResult();
            return new HashSet<TopicPartition> { Partition0 };
        });
        await sawRevokedPass.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        theConsumer.Assignment.Returns(new HashSet<TopicPartition> { Partition0, Partition1 });

        await pausedAgain.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
    }

    [Fact]
    public async Task the_loop_keeps_committing_finished_records_while_paused()
    {
        var committed = new TaskCompletionSource<TopicPartitionOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        theConsumer.When(x => x.CommitAsync(Arg.Any<IEnumerable<TopicPartitionOffset>>(), Arg.Any<CancellationToken>()))
            .Do(ci => committed.TrySetResult(ci.Arg<IEnumerable<TopicPartitionOffset>>().Single()));

        var paused = capture((c, done) =>
            c.Partitions.When(x => x.Pause(Arg.Any<TopicPartition[]>())).Do(ci => done(ci.Arg<TopicPartition[]>())));

        // PerMessage, so a finished record is committed by the loop's next pass
        var kafkaTopic = topic(EndpointMode.BufferedInMemory);
        kafkaTopic.CommitMode = CommitMode.PerMessage;
        await using var listener = listen(kafkaTopic);

        await ((IPausesForBackPressure)listener).PauseForBackPressureAsync();
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        // A record of the backlog, delivered before the pause, finishes while the listener is paused
        var envelope = new Envelope { TopicName = "orders", PartitionId = 0, Offset = 7 };
        trackDelivered(listener, envelope);
        await listener.CompleteAsync(envelope);

        (await committed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token))
            .ShouldBe(new TopicPartitionOffset("orders", 0, 8));
    }

    // What the consume loop does for every record it hands the receiver
    private static void trackDelivered(KafkaListener listener, Envelope envelope)
    {
        listener.Committer.Track(envelope.TopicName!, envelope.PartitionId!.Value, envelope.Offset);
    }
}
