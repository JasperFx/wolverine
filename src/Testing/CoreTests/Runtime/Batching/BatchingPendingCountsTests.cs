using Wolverine.Runtime;
using Wolverine.Runtime.Batching;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Runtime.Batching;

/// <summary>
/// CritterWatch#942 — the per-listener pending count that folds message-batching pipeline depth
/// into ListeningAgent.QueueCount so back-pressure can see past the (deliberately unbounded,
/// GH-3287) batch execution queue.
/// </summary>
public class BatchingPendingCountsTests
{
    private static readonly Uri Address = new("stub://one");
    private static readonly Uri OtherAddress = new("stub://two");

    [Fact]
    public void counts_per_listener_address()
    {
        var counts = new BatchingPendingCounts();

        counts.Increment(Address);
        counts.Increment(Address);
        counts.Increment(OtherAddress);

        counts.PendingFor(Address).ShouldBe(2);
        counts.PendingFor(OtherAddress).ShouldBe(1);
    }

    [Fact]
    public void null_address_is_ignored_local_publishers_are_never_counted()
    {
        var counts = new BatchingPendingCounts();

        counts.Increment(null);
        counts.Decrement(null);

        counts.PendingFor(Address).ShouldBe(0);
    }

    [Fact]
    public void decrement_clamps_at_zero()
    {
        var counts = new BatchingPendingCounts();

        counts.Decrement(Address);
        counts.PendingFor(Address).ShouldBe(0);

        counts.Increment(Address);
        counts.Decrement(Address);
        counts.Decrement(Address);
        counts.PendingFor(Address).ShouldBe(0);
    }

    [Fact]
    public void settle_batch_decrements_each_member_against_its_own_listener()
    {
        var counts = new BatchingPendingCounts();

        var one = envelopeFrom(Address);
        var two = envelopeFrom(Address);
        var three = envelopeFrom(OtherAddress);
        var local = new Envelope(new object()); // no listener — a local send, never counted

        counts.Increment(one.Listener!.Address);
        counts.Increment(two.Listener!.Address);
        counts.Increment(three.Listener!.Address);

        var batch = new Envelope { Batch = [one, two, three, local] };
        counts.SettleBatch(batch);

        counts.PendingFor(Address).ShouldBe(0);
        counts.PendingFor(OtherAddress).ShouldBe(0);
    }

    [Fact]
    public void settle_batch_is_idempotent_per_batch_envelope()
    {
        var counts = new BatchingPendingCounts();

        var one = envelopeFrom(Address);
        var two = envelopeFrom(Address);
        counts.Increment(Address);
        counts.Increment(Address);
        counts.Increment(Address); // a third member still in a DIFFERENT, unfinished batch

        var batch = new Envelope { Batch = [one, two] };

        // A double CompleteAsync (success continuation + dead-letter path racing, or a retried
        // completion block) must not drive the count below the genuinely-pending third member.
        counts.SettleBatch(batch);
        counts.SettleBatch(batch);

        counts.PendingFor(Address).ShouldBe(1);
    }

    [Fact]
    public void settle_batch_ignores_a_non_batch_envelope()
    {
        var counts = new BatchingPendingCounts();
        counts.Increment(Address);

        counts.SettleBatch(envelopeFrom(Address));

        counts.PendingFor(Address).ShouldBe(1);
    }

    // GH-4397 — the per-pipeline count, which does not care where a member came from

    private const string BatchType = "pending-item-batch";

    [Fact]
    public void pipeline_counts_every_member_including_local_sends()
    {
        var counts = new BatchingPendingCounts();
        var pipeline = counts.RegisterPipeline(typeof(PendingItem), BatchType);

        // What BatchingProcessor does per member: one from a listener, one published locally
        var remote = envelopeFrom(Address);
        counts.Increment(remote.Listener!.Address);
        pipeline.Increment();

        var local = new Envelope(new PendingItem("local"));
        counts.Increment(local.Listener?.Address);
        pipeline.Increment();

        counts.PendingForBatchedMessage<PendingItem>().ShouldBe(2);
        counts.TotalPendingBatchMembers.ShouldBe(2);

        // The listener count keeps its back-pressure meaning: only what arrived through that listener
        counts.PendingFor(Address).ShouldBe(1);
    }

    [Fact]
    public void settling_a_counted_batch_releases_its_members_once()
    {
        var counts = new BatchingPendingCounts();
        var pipeline = counts.RegisterPipeline(typeof(PendingItem), BatchType);
        pipeline.Increment();
        pipeline.Increment();
        pipeline.Increment(); // a third member still waiting in the batching channel

        var batch = countedBatch(new Envelope(new PendingItem("one")), new Envelope(new PendingItem("two")));

        counts.SettleBatch(batch);
        counts.SettleBatch(batch);

        counts.PendingForBatchedMessage(typeof(PendingItem)).ShouldBe(1);
    }

    [Fact]
    public void a_batch_nobody_counted_releases_nothing()
    {
        var counts = new BatchingPendingCounts();
        var pipeline = counts.RegisterPipeline(typeof(PendingItem), BatchType);
        pipeline.Increment();
        pipeline.Increment();

        var uncounted = new Envelope(new PendingItem[1], [new Envelope(new PendingItem("one"))])
        {
            MessageType = BatchType
        };

        counts.SettleBatch(uncounted);

        counts.PendingForBatchedMessage<PendingItem>().ShouldBe(2);
    }

    [Fact]
    public void a_replayed_batch_is_pending_until_its_own_terminal()
    {
        var counts = new BatchingPendingCounts();
        counts.RegisterPipeline(typeof(PendingItem), BatchType);

        var reduced = new Envelope(new PendingItem[2],
            [new Envelope(new PendingItem("one")), new Envelope(new PendingItem("two"))])
        {
            MessageType = BatchType
        };

        counts.CountReplayedBatch(reduced);
        counts.PendingForBatchedMessage<PendingItem>().ShouldBe(2);

        counts.SettleBatch(reduced);
        counts.PendingForBatchedMessage<PendingItem>().ShouldBe(0);
    }

    [Fact]
    public void a_replayed_batch_for_an_unregistered_pipeline_is_not_counted()
    {
        var counts = new BatchingPendingCounts();

        var reduced = new Envelope(new PendingItem[1], [new Envelope(new PendingItem("one"))])
        {
            MessageType = "nothing-registered-this"
        };

        counts.CountReplayedBatch(reduced);
        counts.SettleBatch(reduced);

        counts.TotalPendingBatchMembers.ShouldBe(0);
    }

    [Fact]
    public void pipeline_count_clamps_at_zero()
    {
        var counts = new BatchingPendingCounts();
        var pipeline = counts.RegisterPipeline(typeof(PendingItem), BatchType);

        pipeline.Decrement();
        counts.PendingForBatchedMessage<PendingItem>().ShouldBe(0);

        pipeline.Increment();
        counts.SettleBatch(countedBatch(new Envelope(new PendingItem("one")), new Envelope(new PendingItem("two"))));
        counts.PendingForBatchedMessage<PendingItem>().ShouldBe(0);
    }

    [Fact]
    public void registering_the_same_pipeline_twice_shares_one_counter()
    {
        // BatchingProcessor can be built more than once under a startup race
        var counts = new BatchingPendingCounts();

        var first = counts.RegisterPipeline(typeof(PendingItem), BatchType);
        var second = counts.RegisterPipeline(typeof(PendingItem), BatchType);

        second.ShouldBeSameAs(first);
    }

    [Fact]
    public void an_element_type_with_no_pipeline_has_nothing_pending()
    {
        new BatchingPendingCounts().PendingForBatchedMessage<string>().ShouldBe(0);
    }

    private static Envelope countedBatch(params Envelope[] members)
    {
        return new Envelope(new PendingItem[members.Length], members)
        {
            MessageType = BatchType,
            BatchPipelineCounted = true
        };
    }

    public record PendingItem(string Name);

    private static Envelope envelopeFrom(Uri address)
    {
        return new Envelope(new object()) { Listener = new StubListener(address) };
    }

    private class StubListener : IListener
    {
        public StubListener(Uri address)
        {
            Address = address;
        }

        public Uri Address { get; }

        public IHandlerPipeline? Pipeline => null;

        public ValueTask CompleteAsync(Envelope envelope) => ValueTask.CompletedTask;

        public ValueTask DeferAsync(Envelope envelope) => ValueTask.CompletedTask;

        public ValueTask StopAsync() => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
