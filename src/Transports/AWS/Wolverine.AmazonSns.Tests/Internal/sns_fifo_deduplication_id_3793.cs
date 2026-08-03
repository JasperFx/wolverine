using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.AmazonSns.Internal;

namespace Wolverine.AmazonSns.Tests.Internal;

// GH-3793. Two separate SNS defects, both verified against LocalStack:
//
//  1. A FIFO topic without ContentBasedDeduplication rejects any publish that carries no
//     MessageDeduplicationId ("The topic should either have ContentBasedDeduplication enabled or
//     MessageDeduplicationId provided explicitly"). Wolverine's circuit-resume ping never had one,
//     so a latched sender could not probe its way back on such a topic.
//  2. A *standard* topic rejects a MessageDeduplicationId outright ("the request includes
//     MessageDeduplicationId parameter that is not valid for this topic type"), so the mapping has
//     to be gated on the topic type the way AmazonSqsQueue already gates it.
public class sns_fifo_deduplication_id_3793
{
    private static AmazonSnsTopic TopicFor(string name)
    {
        return new AmazonSnsTopic(name, new AmazonSnsTransport())
        {
            Mapper = new DefaultSnsEnvelopeMapper()
        };
    }

    private static string? DeduplicationIdFor(AmazonSnsTopic topic, Envelope envelope)
    {
        var batch = new OutgoingSnsBatch(topic, NullLogger.Instance, [envelope]);
        return batch.Request.PublishBatchRequestEntries.ShouldHaveSingleItem().MessageDeduplicationId;
    }

    private static Envelope theEnvelope()
    {
        return new Envelope
        {
            Data = [1, 2, 3],
            MessageType = "probe-message",
            Destination = new Uri("sns://probe-topic")
        };
    }

    [Fact]
    public void fifo_detection_follows_the_required_suffix()
    {
        TopicFor("orders.fifo").IsFifoTopic.ShouldBeTrue();
        TopicFor("orders").IsFifoTopic.ShouldBeFalse();
    }

    [Fact]
    public void an_explicit_deduplication_id_still_wins_on_a_fifo_topic()
    {
        var envelope = theEnvelope();
        envelope.DeduplicationId = "dedup-1";

        DeduplicationIdFor(TopicFor("orders.fifo"), envelope).ShouldBe("dedup-1");
    }

    [Fact]
    public void a_standard_topic_never_gets_a_deduplication_id()
    {
        var envelope = theEnvelope();
        envelope.DeduplicationId = "dedup-1";

        DeduplicationIdFor(TopicFor("orders"), envelope).ShouldBeNull();
    }

    [Fact]
    public void a_ping_falls_back_to_the_envelope_id_on_a_fifo_topic()
    {
        var ping = Envelope.ForPing(new Uri("sns://orders.fifo"));

        DeduplicationIdFor(TopicFor("orders.fifo"), ping).ShouldBe(ping.Id.ToString());
    }

    [Fact]
    public void two_pings_never_share_a_deduplication_id()
    {
        // Content-based deduplication would collapse these into one -- every ping body is the
        // same four bytes -- which is exactly why the ping needs an explicit, unique id.
        var topic = TopicFor("orders.fifo");

        DeduplicationIdFor(topic, Envelope.ForPing(new Uri("sns://orders.fifo")))
            .ShouldNotBe(DeduplicationIdFor(topic, Envelope.ForPing(new Uri("sns://orders.fifo"))));
    }

    [Fact]
    public void an_ordinary_envelope_with_no_deduplication_id_still_gets_none()
    {
        // Only the ping gets the fallback. A user publishing to a FIFO topic with
        // ContentBasedDeduplication enabled must keep getting content-based behavior.
        DeduplicationIdFor(TopicFor("orders.fifo"), theEnvelope()).ShouldBeNull();
    }
}
