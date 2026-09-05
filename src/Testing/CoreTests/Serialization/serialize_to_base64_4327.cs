using JasperFx.Core;
using Shouldly;
using Wolverine;
using Wolverine.Runtime.Serialization;
using Xunit;

namespace CoreTests.Serialization;

// GH-4327: SerializeToBase64 writes straight from the stream buffer to the base64 string instead
// of Serialize()'s byte[] then Convert.ToBase64String -- one payload-sized allocation instead of
// three. Because it uses GetBuffer() rather than ToArray(), the length bound is the whole
// correctness question: an off-by-one there silently ships the MemoryStream's uninitialized slack
// as trailing garbage. These tests pin the byte-for-byte equivalence with the old two-step form.
public class serialize_to_base64_4327
{
    private static Envelope envelopeWithPayload(int payloadSize)
    {
        var envelope = new Envelope
        {
            SentAt = DateTime.Today.ToUniversalTime(),
            Data = payloadSize == 0 ? [] : Enumerable.Range(0, payloadSize).Select(x => (byte)(x % 251)).ToArray(),
            Destination = "sqs://queue/incoming".ToUri(),
            ReplyUri = "sqs://queue/replies".ToUri(),
            MessageType = "some-message",
            ContentType = "application/json"
        };

        envelope.Headers.Add("name", "Jeremy");
        return envelope;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(1024)]
    [InlineData(100_000)]
    public void matches_the_serialize_then_base64_encode_form(int payloadSize)
    {
        var envelope = envelopeWithPayload(payloadSize);

        var expected = Convert.ToBase64String(EnvelopeSerializer.Serialize(envelope));

        EnvelopeSerializer.SerializeToBase64(envelope).ShouldBe(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(100_000)]
    public void round_trips_through_the_reader(int payloadSize)
    {
        var envelope = envelopeWithPayload(payloadSize);

        var incoming = EnvelopeSerializer.Deserialize(
            Convert.FromBase64String(EnvelopeSerializer.SerializeToBase64(envelope)));

        incoming.Data.ShouldBe(envelope.Data);
        incoming.Destination.ShouldBe(envelope.Destination);
        incoming.ReplyUri.ShouldBe(envelope.ReplyUri);
        incoming.MessageType.ShouldBe(envelope.MessageType);
        incoming.Headers["name"].ShouldBe("Jeremy");
    }
}
