using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Kafka;
using Wolverine.Kafka.Internals;

namespace Wolverine.Kafka.Tests;

// GH-3150: unit coverage for the commit-strategy resolution, specific-offset commits, the contiguous
// watermark, and shutdown flush — all without a broker (mocked IConsumer).
public class kafka_offset_committer
{
    private readonly IConsumer<string, byte[]> theConsumer = Substitute.For<IConsumer<string, byte[]>>();

    private KafkaOffsetCommitter committerFor(CommitMode mode, ConsumerConfig? config = null, int batchCount = 100,
        TimeSpan? interval = null)
    {
        config ??= new ConsumerConfig();
        KafkaOffsetCommitter.ApplyTo(config, mode);
        return new KafkaOffsetCommitter(theConsumer, config, mode, batchCount,
            interval ?? TimeSpan.FromSeconds(5), NullLogger.Instance);
    }

    [Fact]
    public void apply_to_store_then_auto_flush_sets_autocommit_and_disables_offset_store()
    {
        var config = new ConsumerConfig();
        KafkaOffsetCommitter.ApplyTo(config, CommitMode.StoreThenAutoFlush);

        config.EnableAutoCommit.ShouldBe(true);
        config.EnableAutoOffsetStore.ShouldBe(false);
    }

    [Fact]
    public void apply_to_per_message_disables_autocommit()
    {
        var config = new ConsumerConfig();
        KafkaOffsetCommitter.ApplyTo(config, CommitMode.PerMessage);
        config.EnableAutoCommit.ShouldBe(false);
    }

    [Fact]
    public void apply_to_respects_explicit_user_autocommit()
    {
        var config = new ConsumerConfig { EnableAutoCommit = true };
        KafkaOffsetCommitter.ApplyTo(config, CommitMode.PerMessage);
        // user's choice is untouched
        config.EnableAutoCommit.ShouldBe(true);
    }

    [Fact]
    public void resolve_strategy_matrix()
    {
        KafkaOffsetCommitter.ResolveStrategy(new ConsumerConfig { EnableAutoCommit = true },
            CommitMode.StoreThenAutoFlush).ShouldBe(KafkaOffsetCommitter.Strategy.Automatic);

        KafkaOffsetCommitter.ResolveStrategy(new ConsumerConfig { EnableAutoCommit = true, EnableAutoOffsetStore = false },
            CommitMode.StoreThenAutoFlush).ShouldBe(KafkaOffsetCommitter.Strategy.Store);

        KafkaOffsetCommitter.ResolveStrategy(new ConsumerConfig { EnableAutoCommit = false },
            CommitMode.PerMessage).ShouldBe(KafkaOffsetCommitter.Strategy.PerMessage);

        KafkaOffsetCommitter.ResolveStrategy(new ConsumerConfig { EnableAutoCommit = false },
            CommitMode.BatchCount).ShouldBe(KafkaOffsetCommitter.Strategy.Batch);
    }

    [Fact]
    public void store_mode_stores_specific_offset_plus_one_and_never_commits()
    {
        var committer = committerFor(CommitMode.StoreThenAutoFlush);

        committer.Complete("topic-a", 2, 41);

        // Kafka convention: committed/stored offset is last-processed + 1
        theConsumer.Received(1).StoreOffset(
            Arg.Is<TopicPartitionOffset>(t => t.Topic == "topic-a" && t.Partition.Value == 2 && t.Offset.Value == 42));
        theConsumer.DidNotReceive().Commit(Arg.Any<IEnumerable<TopicPartitionOffset>>());
    }

    [Fact]
    public void per_message_commits_specific_offset_plus_one()
    {
        var committer = committerFor(CommitMode.PerMessage);

        committer.Complete("topic-a", 0, 9);

        theConsumer.Received(1).Commit(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
            list.Single().Topic == "topic-a" && list.Single().Partition.Value == 0 && list.Single().Offset.Value == 10));
    }

    [Fact]
    public void automatic_mode_neither_stores_nor_commits()
    {
        var committer = committerFor(CommitMode.StoreThenAutoFlush, new ConsumerConfig { EnableAutoCommit = true });
        committer.ResolvedStrategy.ShouldBe(KafkaOffsetCommitter.Strategy.Automatic);

        committer.Complete("topic-a", 0, 5);

        theConsumer.DidNotReceive().StoreOffset(Arg.Any<TopicPartitionOffset>());
        theConsumer.DidNotReceive().Commit(Arg.Any<IEnumerable<TopicPartitionOffset>>());
    }

    [Fact]
    public void batch_count_commits_watermark_after_threshold()
    {
        var committer = committerFor(CommitMode.BatchCount, batchCount: 3);

        committer.Complete("t", 0, 100);
        committer.Complete("t", 0, 101);
        theConsumer.DidNotReceive().Commit(Arg.Any<IEnumerable<TopicPartitionOffset>>());

        committer.Complete("t", 0, 102); // hits batch size 3

        theConsumer.Received(1).Commit(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
            list.Single().Offset.Value == 103)); // watermark = last contiguous (102) + 1
    }

    [Fact]
    public void batch_count_never_commits_ahead_of_an_in_flight_offset()
    {
        var committer = committerFor(CommitMode.BatchCount, batchCount: 3);

        // Delivered in consume order; 101 will stay in flight while later offsets finish first.
        committer.Track("t", 0, 100);
        committer.Track("t", 0, 101);
        committer.Track("t", 0, 102);
        committer.Track("t", 0, 103);

        // Out-of-order completion: 100, 102, 103 — 101 is still in flight.
        committer.Complete("t", 0, 100);
        committer.Complete("t", 0, 102);
        committer.Complete("t", 0, 103); // threshold reached, but 101 is still in flight

        // The committed position may only advance to the lowest in-flight offset (101), never past it.
        theConsumer.Received(1).Commit(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
            list.Single().Offset.Value == 101));

        theConsumer.ClearReceivedCalls();

        // Now 101 completes -> nothing in flight -> the position jumps to the high-water 103 + 1 = 104.
        committer.Complete("t", 0, 101);
        committer.Flush();

        theConsumer.Received(1).Commit(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
            list.Single().Offset.Value == 104));
    }

    [Fact]
    public void per_message_never_commits_ahead_of_an_in_flight_offset()
    {
        var committer = committerFor(CommitMode.PerMessage);

        // Delivered in order 0,1,2; a later message completes first under concurrency.
        committer.Track("t", 0, 0);
        committer.Track("t", 0, 1);
        committer.Track("t", 0, 2);

        // 2 finishes while 0 and 1 are still in flight -> nothing may be committed yet.
        committer.Complete("t", 0, 2);
        theConsumer.DidNotReceive().Commit(Arg.Any<IEnumerable<TopicPartitionOffset>>());

        // 0 finishes -> safe to resume from the next in-flight offset (1).
        committer.Complete("t", 0, 0);
        theConsumer.Received(1).Commit(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
            list.Single().Offset.Value == 1));

        theConsumer.ClearReceivedCalls();

        // 1 finishes -> nothing in flight -> resume past the highest delivered (2 + 1 = 3).
        committer.Complete("t", 0, 1);
        theConsumer.Received(1).Commit(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
            list.Single().Offset.Value == 3));
    }

    [Fact]
    public void store_never_stores_ahead_of_an_in_flight_offset()
    {
        var committer = committerFor(CommitMode.StoreThenAutoFlush);

        // Delivered in order 0,1,2; out-of-order completion under concurrency.
        committer.Track("t", 0, 0);
        committer.Track("t", 0, 1);
        committer.Track("t", 0, 2);

        // 2 finishes while 0 and 1 are still in flight -> nothing may be stored yet.
        committer.Complete("t", 0, 2);
        theConsumer.DidNotReceive().StoreOffset(Arg.Any<TopicPartitionOffset>());

        // 0 finishes -> safe to store the next in-flight offset (1).
        committer.Complete("t", 0, 0);
        theConsumer.Received(1).StoreOffset(Arg.Is<TopicPartitionOffset>(t => t.Offset.Value == 1));

        theConsumer.ClearReceivedCalls();

        // 1 finishes -> nothing in flight -> store past the highest delivered (2 + 1 = 3).
        committer.Complete("t", 0, 1);
        theConsumer.Received(1).StoreOffset(Arg.Is<TopicPartitionOffset>(t => t.Offset.Value == 3));
    }

    [Fact]
    public void per_message_tolerates_non_contiguous_offsets_from_a_compacted_topic()
    {
        var committer = committerFor(CommitMode.PerMessage);

        // A compacted / transactional topic hands out gaps (1 and 2 were compacted or were control
        // records) — the watermark must not stall waiting for offsets that will never be delivered.
        committer.Track("t", 0, 0);
        committer.Track("t", 0, 3);

        committer.Complete("t", 0, 0);
        theConsumer.Received(1).Commit(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
            list.Single().Offset.Value == 3)); // resume from the next in-flight delivered offset

        theConsumer.ClearReceivedCalls();

        committer.Complete("t", 0, 3);
        theConsumer.Received(1).Commit(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
            list.Single().Offset.Value == 4)); // high-water + 1, gaps skipped
    }

    [Fact]
    public void flush_commits_pending_batch_watermark()
    {
        var committer = committerFor(CommitMode.BatchCount, batchCount: 1000);

        committer.Complete("t", 1, 7);
        committer.Complete("t", 1, 8);
        theConsumer.DidNotReceive().Commit(Arg.Any<IEnumerable<TopicPartitionOffset>>());

        committer.Flush();

        theConsumer.Received(1).Commit(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
            list.Single().Topic == "t" && list.Single().Partition.Value == 1 && list.Single().Offset.Value == 9));
    }

    [Fact]
    public void watermark_handles_independent_partitions()
    {
        var committer = committerFor(CommitMode.BatchCount, batchCount: 1000);

        committer.Complete("t", 0, 50);
        committer.Complete("t", 1, 200);
        committer.Flush();

        theConsumer.Received(1).Commit(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
            list.Any(x => x.Partition.Value == 0 && x.Offset.Value == 51) &&
            list.Any(x => x.Partition.Value == 1 && x.Offset.Value == 201)));
    }
}
