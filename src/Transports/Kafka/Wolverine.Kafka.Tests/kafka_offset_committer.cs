using Dekaf;
using Dekaf.Consumer;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Kafka.Internals;

namespace Wolverine.Kafka.Tests;

// GH-3150: unit coverage for the commit-strategy resolution, specific-offset commits, the contiguous
// watermark, and shutdown flush — all without a broker (mocked IKafkaConsumer).
public class kafka_offset_committer
{
    private readonly IKafkaConsumer<string, byte[]> theConsumer = Substitute.For<IKafkaConsumer<string, byte[]>>();

    private KafkaOffsetCommitter committerFor(CommitMode mode, KafkaConsumerOptions? config = null, int batchCount = 100,
        TimeSpan? interval = null)
    {
        config ??= new KafkaConsumerOptions();
        KafkaOffsetCommitter.ApplyTo(config, mode);
        return new KafkaOffsetCommitter(theConsumer, config, mode, batchCount,
            interval ?? TimeSpan.FromSeconds(5), NullLogger.Instance);
    }

    // A handler finishing a record, then the consume loop's next pass
    private static async Task completeAndPassAsync(KafkaOffsetCommitter committer, string topic, int partition,
        long offset)
    {
        committer.Complete(topic, partition, offset);
        await committer.CommitCompletedAsync();
    }

    [Fact]
    public async Task a_completion_is_only_committed_by_the_loop_pass()
    {
        var committer = committerFor(CommitMode.PerMessage);

        committer.Complete("t", 0, 9);
        await theConsumer.DidNotReceive()
            .CommitAsync(Arg.Any<IEnumerable<TopicPartitionOffset>>(), Arg.Any<CancellationToken>());

        await committer.CommitCompletedAsync();
        await theConsumer.Received(1).CommitAsync(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
            list.Single().Offset == 10), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task one_pass_commits_every_partition_in_one_call()
    {
        var committer = committerFor(CommitMode.PerMessage);

        committer.Complete("t", 0, 9);
        committer.Complete("t", 1, 4);
        await committer.CommitCompletedAsync();

        await theConsumer.Received(1).CommitAsync(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
            list.Count() == 2), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task a_revocation_commits_completions_the_loop_has_not_applied_yet()
    {
        var committer = committerFor(CommitMode.PerMessage);

        committer.Track("t", 1, 5);
        committer.Complete("t", 1, 5);
        await committer.PartitionsRevokedAsync([new TopicPartition("t", 1)]);

        await theConsumer.Received(1).CommitAsync(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
            list.Single().Offset == 6), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task batch_interval_commits_on_a_quiet_pass_once_the_interval_passes()
    {
        var committer = committerFor(CommitMode.BatchInterval, interval: TimeSpan.FromSeconds(1));

        await completeAndPassAsync(committer, "t", 0, 7);
        await theConsumer.DidNotReceive()
            .CommitAsync(Arg.Any<IEnumerable<TopicPartitionOffset>>(), Arg.Any<CancellationToken>());

        await Task.Delay(1200, TestContext.Current.CancellationToken);
        await committer.CommitCompletedAsync();

        await theConsumer.Received(1).CommitAsync(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
            list.Single().Offset == 8), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void apply_to_store_then_auto_flush_sets_autocommit_and_disables_offset_store()
    {
        var config = new KafkaConsumerOptions();
        KafkaOffsetCommitter.ApplyTo(config, CommitMode.StoreThenAutoFlush);

        config.OffsetCommitMode.ShouldBe(OffsetCommitMode.Auto);
        config.EnableAutoOffsetStore.ShouldBe(false);
    }

    [Fact]
    public void apply_to_per_message_disables_autocommit()
    {
        var config = new KafkaConsumerOptions();
        KafkaOffsetCommitter.ApplyTo(config, CommitMode.PerMessage);
        config.OffsetCommitMode.ShouldBe(OffsetCommitMode.Manual);
    }

    [Fact]
    public void apply_to_respects_explicit_user_autocommit()
    {
        var config = new KafkaConsumerOptions { OffsetCommitMode = OffsetCommitMode.Auto };
        KafkaOffsetCommitter.ApplyTo(config, CommitMode.PerMessage);
        // user's choice is untouched
        config.OffsetCommitMode.ShouldBe(OffsetCommitMode.Auto);
    }

    [Fact]
    public void resolve_strategy_matrix()
    {
        KafkaOffsetCommitter.ResolveStrategy(new KafkaConsumerOptions { OffsetCommitMode = OffsetCommitMode.Auto },
            CommitMode.StoreThenAutoFlush).ShouldBe(KafkaOffsetCommitter.Strategy.Automatic);

        KafkaOffsetCommitter.ResolveStrategy(new KafkaConsumerOptions { OffsetCommitMode = OffsetCommitMode.Auto, EnableAutoOffsetStore = false },
            CommitMode.StoreThenAutoFlush).ShouldBe(KafkaOffsetCommitter.Strategy.Store);

        KafkaOffsetCommitter.ResolveStrategy(new KafkaConsumerOptions { OffsetCommitMode = OffsetCommitMode.Manual },
            CommitMode.PerMessage).ShouldBe(KafkaOffsetCommitter.Strategy.PerMessage);

        KafkaOffsetCommitter.ResolveStrategy(new KafkaConsumerOptions { OffsetCommitMode = OffsetCommitMode.Manual },
            CommitMode.BatchCount).ShouldBe(KafkaOffsetCommitter.Strategy.Batch);
    }

    [Fact]
    public async Task store_mode_stores_specific_offset_plus_one_and_never_commits()
    {
        var committer = committerFor(CommitMode.StoreThenAutoFlush);

        await completeAndPassAsync(committer, "topic-a", 2, 41);

        // Kafka convention: committed/stored offset is last-processed + 1
        theConsumer.Received(1).StoreOffset(
            Arg.Is<TopicPartitionOffset>(t => t.Topic == "topic-a" && t.Partition == 2 && t.Offset == 42));
        await theConsumer.DidNotReceive()
            .CommitAsync(Arg.Any<IEnumerable<TopicPartitionOffset>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task per_message_commits_specific_offset_plus_one()
    {
        var committer = committerFor(CommitMode.PerMessage);

        await completeAndPassAsync(committer, "topic-a", 0, 9);

        await theConsumer.Received(1).CommitAsync(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
                list.Single().Topic == "topic-a" && list.Single().Partition == 0 && list.Single().Offset == 10),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task automatic_mode_neither_stores_nor_commits()
    {
        var committer = committerFor(CommitMode.StoreThenAutoFlush, new KafkaConsumerOptions { OffsetCommitMode = OffsetCommitMode.Auto });
        committer.ResolvedStrategy.ShouldBe(KafkaOffsetCommitter.Strategy.Automatic);

        await completeAndPassAsync(committer, "topic-a", 0, 5);

        theConsumer.DidNotReceive().StoreOffset(Arg.Any<TopicPartitionOffset>());
        await theConsumer.DidNotReceive()
            .CommitAsync(Arg.Any<IEnumerable<TopicPartitionOffset>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task batch_count_commits_watermark_after_threshold()
    {
        var committer = committerFor(CommitMode.BatchCount, batchCount: 3);

        await completeAndPassAsync(committer, "t", 0, 100);
        await completeAndPassAsync(committer, "t", 0, 101);
        await theConsumer.DidNotReceive()
            .CommitAsync(Arg.Any<IEnumerable<TopicPartitionOffset>>(), Arg.Any<CancellationToken>());

        await completeAndPassAsync(committer, "t", 0, 102); // hits batch size 3

        await theConsumer.Received(1).CommitAsync(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
            list.Single().Offset == 103), Arg.Any<CancellationToken>()); // watermark = last contiguous (102) + 1
    }

    [Fact]
    public async Task batch_count_never_commits_ahead_of_an_in_flight_offset()
    {
        var committer = committerFor(CommitMode.BatchCount, batchCount: 3);

        // Delivered in consume order; 101 will stay in flight while later offsets finish first.
        committer.Track("t", 0, 100);
        committer.Track("t", 0, 101);
        committer.Track("t", 0, 102);
        committer.Track("t", 0, 103);

        // Out-of-order completion: 100, 102, 103 — 101 is still in flight.
        await completeAndPassAsync(committer, "t", 0, 100);
        await completeAndPassAsync(committer, "t", 0, 102);
        await completeAndPassAsync(committer, "t", 0, 103); // threshold reached, but 101 is still in flight

        // The committed position may only advance to the lowest in-flight offset (101), never past it.
        await theConsumer.Received(1).CommitAsync(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
            list.Single().Offset == 101), Arg.Any<CancellationToken>());

        theConsumer.ClearReceivedCalls();

        // Now 101 completes -> nothing in flight -> the position jumps to the high-water 103 + 1 = 104.
        await completeAndPassAsync(committer, "t", 0, 101);
        await committer.FlushAsync();

        await theConsumer.Received(1).CommitAsync(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
            list.Single().Offset == 104), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task per_message_never_commits_ahead_of_an_in_flight_offset()
    {
        var committer = committerFor(CommitMode.PerMessage);

        // Delivered in order 0,1,2; a later message completes first under concurrency.
        committer.Track("t", 0, 0);
        committer.Track("t", 0, 1);
        committer.Track("t", 0, 2);

        // 2 finishes while 0 and 1 are still in flight -> nothing may be committed yet.
        await completeAndPassAsync(committer, "t", 0, 2);
        await theConsumer.DidNotReceive()
            .CommitAsync(Arg.Any<IEnumerable<TopicPartitionOffset>>(), Arg.Any<CancellationToken>());

        // 0 finishes -> safe to resume from the next in-flight offset (1).
        await completeAndPassAsync(committer, "t", 0, 0);
        await theConsumer.Received(1).CommitAsync(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
            list.Single().Offset == 1), Arg.Any<CancellationToken>());

        theConsumer.ClearReceivedCalls();

        // 1 finishes -> nothing in flight -> resume past the highest delivered (2 + 1 = 3).
        await completeAndPassAsync(committer, "t", 0, 1);
        await theConsumer.Received(1).CommitAsync(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
            list.Single().Offset == 3), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task store_never_stores_ahead_of_an_in_flight_offset()
    {
        var committer = committerFor(CommitMode.StoreThenAutoFlush);

        // Delivered in order 0,1,2; out-of-order completion under concurrency.
        committer.Track("t", 0, 0);
        committer.Track("t", 0, 1);
        committer.Track("t", 0, 2);

        // 2 finishes while 0 and 1 are still in flight -> nothing may be stored yet.
        await completeAndPassAsync(committer, "t", 0, 2);
        theConsumer.DidNotReceive().StoreOffset(Arg.Any<TopicPartitionOffset>());

        // 0 finishes -> safe to store the next in-flight offset (1).
        await completeAndPassAsync(committer, "t", 0, 0);
        theConsumer.Received(1).StoreOffset(Arg.Is<TopicPartitionOffset>(t => t.Offset == 1));

        theConsumer.ClearReceivedCalls();

        // 1 finishes -> nothing in flight -> store past the highest delivered (2 + 1 = 3).
        await completeAndPassAsync(committer, "t", 0, 1);
        theConsumer.Received(1).StoreOffset(Arg.Is<TopicPartitionOffset>(t => t.Offset == 3));
    }

    [Fact]
    public async Task per_message_tolerates_non_contiguous_offsets_from_a_compacted_topic()
    {
        var committer = committerFor(CommitMode.PerMessage);

        // A compacted / transactional topic hands out gaps (1 and 2 were compacted or were control
        // records) — the watermark must not stall waiting for offsets that will never be delivered.
        committer.Track("t", 0, 0);
        committer.Track("t", 0, 3);

        await completeAndPassAsync(committer, "t", 0, 0);
        await theConsumer.Received(1).CommitAsync(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
                list.Single().Offset == 3),
            Arg.Any<CancellationToken>()); // resume from the next in-flight delivered offset

        theConsumer.ClearReceivedCalls();

        await completeAndPassAsync(committer, "t", 0, 3);
        await theConsumer.Received(1).CommitAsync(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
            list.Single().Offset == 4), Arg.Any<CancellationToken>()); // high-water + 1, gaps skipped
    }

    [Fact]
    public async Task flush_commits_pending_batch_watermark()
    {
        var committer = committerFor(CommitMode.BatchCount, batchCount: 1000);

        await completeAndPassAsync(committer, "t", 1, 7);
        await completeAndPassAsync(committer, "t", 1, 8);
        await theConsumer.DidNotReceive()
            .CommitAsync(Arg.Any<IEnumerable<TopicPartitionOffset>>(), Arg.Any<CancellationToken>());

        await committer.FlushAsync();

        await theConsumer.Received(1).CommitAsync(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
                list.Single().Topic == "t" && list.Single().Partition == 1 && list.Single().Offset == 9),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task watermark_handles_independent_partitions()
    {
        var committer = committerFor(CommitMode.BatchCount, batchCount: 1000);

        await completeAndPassAsync(committer, "t", 0, 50);
        await completeAndPassAsync(committer, "t", 1, 200);
        await committer.FlushAsync();

        await theConsumer.Received(1).CommitAsync(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
            list.Any(x => x.Partition == 0 && x.Offset == 51) &&
            list.Any(x => x.Partition == 1 && x.Offset == 201)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task revocation_commits_finished_batch_offsets_of_the_revoked_partitions_only()
    {
        var committer = committerFor(CommitMode.BatchCount, batchCount: 1000);

        await completeAndPassAsync(committer, "t", 1, 7);
        await completeAndPassAsync(committer, "t", 1, 8);
        await completeAndPassAsync(committer, "t", 2, 3);

        await committer.PartitionsRevokedAsync([new TopicPartition("t", 1)]);

        await theConsumer.Received(1).CommitAsync(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
                list.Single().Partition == 1 && list.Single().Offset == 9),
            Arg.Any<CancellationToken>());

        theConsumer.ClearReceivedCalls();

        // The revoked partition is no longer tracked, so the shutdown flush only has partition 2 left
        await committer.FlushAsync();
        await theConsumer.Received(1).CommitAsync(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
                list.Single().Partition == 2 && list.Single().Offset == 4),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task a_completion_after_revocation_is_not_committed()
    {
        var committer = committerFor(CommitMode.PerMessage);

        committer.Track("t", 1, 5);
        await committer.PartitionsRevokedAsync([new TopicPartition("t", 1)]);
        await completeAndPassAsync(committer, "t", 1, 5);

        await theConsumer.DidNotReceive()
            .CommitAsync(Arg.Any<IEnumerable<TopicPartitionOffset>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task a_reassigned_partition_commits_again()
    {
        var committer = committerFor(CommitMode.PerMessage);
        var partition = new TopicPartition("t", 1);

        await committer.PartitionsRevokedAsync([partition]);

        committer.Track("t", 1, 20);
        await completeAndPassAsync(committer, "t", 1, 20);

        await theConsumer.Received(1).CommitAsync(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
            list.Single().Offset == 21), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task a_late_completion_cannot_move_a_reassigned_partition_past_redelivered_records()
    {
        var committer = committerFor(CommitMode.PerMessage);
        var partition = new TopicPartition("t", 1);

        for (var offset = 100; offset <= 110; offset++) committer.Track("t", 1, offset);
        await committer.PartitionsRevokedAsync([partition]);

        // The partition comes back and 100 is delivered again, then a handler from before the revocation finishes 110
        committer.Track("t", 1, 100);
        await completeAndPassAsync(committer, "t", 1, 110);

        await theConsumer.DidNotReceive()
            .CommitAsync(Arg.Any<IEnumerable<TopicPartitionOffset>>(), Arg.Any<CancellationToken>());

        await completeAndPassAsync(committer, "t", 1, 100);

        await theConsumer.Received(1).CommitAsync(Arg.Is<IEnumerable<TopicPartitionOffset>>(list =>
            list.Single().Offset == 101), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task lost_partitions_are_dropped_without_a_commit()
    {
        var committer = committerFor(CommitMode.BatchCount, batchCount: 1000);

        await completeAndPassAsync(committer, "t", 1, 7);
        committer.PartitionsLost([new TopicPartition("t", 1)]);
        await committer.FlushAsync();
        await completeAndPassAsync(committer, "t", 1, 8);

        await theConsumer.DidNotReceive()
            .CommitAsync(Arg.Any<IEnumerable<TopicPartitionOffset>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task store_strategy_leaves_the_revocation_commit_to_dekaf()
    {
        var committer = committerFor(CommitMode.StoreThenAutoFlush);

        committer.Track("t", 1, 5);
        committer.Track("t", 1, 6);
        await completeAndPassAsync(committer, "t", 1, 6);
        await committer.PartitionsRevokedAsync([new TopicPartition("t", 1)]);

        await theConsumer.DidNotReceive()
            .CommitAsync(Arg.Any<IEnumerable<TopicPartitionOffset>>(), Arg.Any<CancellationToken>());
    }
}
