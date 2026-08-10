using JasperFx.Core;
using Wolverine.Runtime;
using Wolverine.Runtime.Batching;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Runtime.Batching;

/// <summary>
/// GH-3898 — grouped message batches used to drop their members' DeliverBy expiry entirely: the
/// grouped envelope was created with a fresh SentAt and no DeliverBy, and member expiry is only
/// checked at execution time, which for a batched member never happens on the member itself.
/// These cover the assembly-time member shed and the latest-member-expiry batch backstop.
/// </summary>
public class BatchDeliverByExpiryTests
{
    public record Element(string Name);

    private static Envelope liveEnvelope(TimeSpan? deliverWithin = null)
    {
        var envelope = new Envelope(new Element(Guid.NewGuid().ToString()));
        if (deliverWithin.HasValue)
        {
            envelope.DeliverWithin = deliverWithin.Value;
        }

        return envelope;
    }

    private static Envelope expiredEnvelope(TimeSpan? expiredBy = null)
    {
        return new Envelope(new Element(Guid.NewGuid().ToString()))
        {
            DeliverBy = DateTimeOffset.UtcNow.Subtract(expiredBy ?? 1.Minutes())
        };
    }

    [Fact]
    public void partition_returns_the_original_array_when_nothing_is_expired()
    {
        var envelopes = new[] { liveEnvelope(), liveEnvelope(1.Hours()) };

        var (live, expired) = BatchingProcessor<Element>.PartitionByExpiration(envelopes);

        live.ShouldBeSameAs(envelopes);
        expired.ShouldBeEmpty();
    }

    [Fact]
    public void partition_splits_expired_members_from_live_members()
    {
        var staleOne = expiredEnvelope();
        var staleTwo = expiredEnvelope(5.Minutes());
        var fresh = liveEnvelope();
        var freshWithExpiry = liveEnvelope(1.Hours());

        var (live, expired) =
            BatchingProcessor<Element>.PartitionByExpiration([staleOne, fresh, staleTwo, freshWithExpiry]);

        live.ShouldBe([fresh, freshWithExpiry]);
        expired.ShouldBe([staleOne, staleTwo]);
    }

    [Fact]
    public void partition_can_expire_every_member()
    {
        var staleOne = expiredEnvelope();
        var staleTwo = expiredEnvelope();

        var (live, expired) = BatchingProcessor<Element>.PartitionByExpiration([staleOne, staleTwo]);

        live.ShouldBeEmpty();
        expired.ShouldBe([staleOne, staleTwo]);
    }

    [Fact]
    public void carrier_holds_the_expired_members_and_is_itself_expired()
    {
        var staleOne = expiredEnvelope(10.Minutes());
        var staleTwo = expiredEnvelope(2.Minutes()); // the latest member expiry
        var staleThree = expiredEnvelope(30.Minutes());

        var carrier = BatchingProcessor<Element>.BuildExpiredMemberCarrier([staleOne, staleTwo, staleThree]);

        // The members ride in Batch so the batch terminal (CompleteAsync) settles their
        // back-pressure counts and marks them handled through the normal machinery
        carrier.Batch.ShouldBe(new[] { staleOne, staleTwo, staleThree });

        // Already expired — the handler pipeline's execution-time check discards it
        // before any handler could ever run
        carrier.IsExpired().ShouldBeTrue();
        carrier.DeliverBy.ShouldBe(staleTwo.DeliverBy);

        // Never executed, but present and of the element array shape so nothing downstream
        // chokes on a null message
        carrier.Message.ShouldBeOfType<Element[]>().ShouldBeEmpty();
    }

    [Fact]
    public void carrier_marks_its_members_as_in_batch()
    {
        var stale = expiredEnvelope();

        BatchingProcessor<Element>.BuildExpiredMemberCarrier([stale]);

        // The InBatch flag is what routes the members' own CompleteAsync calls to the
        // batch-terminal path instead of completing them individually
        stale.InBatch.ShouldBeTrue();
    }

    [Fact]
    public void settling_the_carrier_drains_the_members_back_pressure_counts()
    {
        var address = new Uri("stub://one");
        var otherAddress = new Uri("stub://two");
        var counts = new BatchingPendingCounts();

        var one = expiredEnvelope();
        one.Listener = new StubListener(address);
        var two = expiredEnvelope();
        two.Listener = new StubListener(address);
        var three = expiredEnvelope();
        three.Listener = new StubListener(otherAddress);

        counts.Increment(address);
        counts.Increment(address);
        counts.Increment(otherAddress);

        var carrier = BatchingProcessor<Element>.BuildExpiredMemberCarrier([one, two, three]);

        // What the batch terminal (BufferedReceiver/DurableReceiver CompleteAsync) does when the
        // carrier's discard completes — shedding must not leak the listeners' pending counts
        counts.SettleBatch(carrier);

        counts.PendingFor(address).ShouldBe(0);
        counts.PendingFor(otherAddress).ShouldBe(0);
    }

    [Fact]
    public void backstop_is_the_latest_member_expiry_when_every_member_has_one()
    {
        var earliest = liveEnvelope(5.Minutes());
        var latest = liveEnvelope(20.Minutes());
        var middle = liveEnvelope(10.Minutes());

        BatchingProcessor<Element>.LatestMemberExpiry([earliest, latest, middle])
            .ShouldBe(latest.DeliverBy);
    }

    [Fact]
    public void backstop_is_null_when_any_member_never_expires()
    {
        var expiring = liveEnvelope(5.Minutes());
        var immortal = liveEnvelope();

        // A member with no DeliverBy never expires, so the batch as a whole must never
        // expire either — a batch-level DeliverBy here would over-shed
        BatchingProcessor<Element>.LatestMemberExpiry([expiring, immortal]).ShouldBeNull();
    }

    [Fact]
    public void backstop_is_null_for_a_missing_or_empty_member_array()
    {
        BatchingProcessor<Element>.LatestMemberExpiry(null).ShouldBeNull();
        BatchingProcessor<Element>.LatestMemberExpiry([]).ShouldBeNull();
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
