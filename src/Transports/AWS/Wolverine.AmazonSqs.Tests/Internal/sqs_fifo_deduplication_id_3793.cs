using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.ComplianceTests;

namespace Wolverine.AmazonSqs.Tests.Internal;

// GH-3793: a FIFO queue that does not have ContentBasedDeduplication turned on rejects any send
// with no MessageDeduplicationId at all ("The queue should either have ContentBasedDeduplication
// enabled or MessageDeduplicationId provided explicitly"). Wolverine's circuit-resume ping carries
// no deduplication id of its own, so a latched sender could never probe its way back on such a
// queue -- the probe itself failed deterministically.
public class sqs_fifo_deduplication_id_3793
{
    private static AmazonSqsQueue QueueFor(string name)
    {
        return new AmazonSqsQueue(name, new AmazonSqsTransport())
        {
            Mapper = new DefaultSqsEnvelopeMapper()
        };
    }

    private static string? DeduplicationIdFor(AmazonSqsQueue queue, Envelope envelope)
    {
        var batch = new OutgoingSqsBatch(queue, NullLogger.Instance, [SqsSendUnit.Whole(queue, envelope)]);
        return batch.Request.Entries.ShouldHaveSingleItem().MessageDeduplicationId;
    }

    [Fact]
    public void an_explicit_deduplication_id_still_wins()
    {
        var envelope = ObjectMother.Envelope();
        envelope.DeduplicationId = "dedup-1";

        DeduplicationIdFor(QueueFor("orders.fifo"), envelope).ShouldBe("dedup-1");
    }

    [Fact]
    public void a_ping_falls_back_to_the_envelope_id_on_a_fifo_queue()
    {
        var ping = Envelope.ForPing(new Uri("sqs://orders.fifo"));

        DeduplicationIdFor(QueueFor("orders.fifo"), ping).ShouldBe(ping.Id.ToString());
    }

    [Fact]
    public void two_pings_never_share_a_deduplication_id()
    {
        // Content-based deduplication would collapse these into one -- every ping body is the
        // same four bytes -- which is exactly why the ping needs an explicit, unique id.
        var queue = QueueFor("orders.fifo");

        DeduplicationIdFor(queue, Envelope.ForPing(new Uri("sqs://orders.fifo")))
            .ShouldNotBe(DeduplicationIdFor(queue, Envelope.ForPing(new Uri("sqs://orders.fifo"))));
    }

    [Fact]
    public void an_ordinary_envelope_with_no_deduplication_id_still_gets_none()
    {
        // Only the ping gets the fallback. A user publishing to a FIFO queue with
        // ContentBasedDeduplication enabled must keep getting content-based behavior.
        var envelope = ObjectMother.Envelope();
        envelope.DeduplicationId = null;

        DeduplicationIdFor(QueueFor("orders.fifo"), envelope).ShouldBeNull();
    }

    [Fact]
    public void a_ping_to_a_standard_queue_gets_no_deduplication_id()
    {
        var ping = Envelope.ForPing(new Uri("sqs://orders"));

        DeduplicationIdFor(QueueFor("orders"), ping).ShouldBeNull();
    }
}
