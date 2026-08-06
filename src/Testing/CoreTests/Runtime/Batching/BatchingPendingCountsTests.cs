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
