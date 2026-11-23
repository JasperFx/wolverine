using CoreTests.Transports;
using Shouldly;
using Wolverine;
using Wolverine.Persistence;
using Wolverine.Persistence.ClaimCheck.Internal;
using Wolverine.Runtime.Serialization;
using Wolverine.Runtime.Serialization.Encryption;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Persistence.ClaimCheck;

/// <summary>
/// GH-5889. The claim-check serializer stamps its token headers onto the envelope WHILE it serializes, and
/// an envelope serializes lazily, on the first read of <see cref="Envelope.Data" />. A sender that maps the
/// envelope's headers onto the broker message before it reads the body (RabbitMQ, inline Azure Service Bus,
/// NATS, Pulsar, Redis, inline GCP Pub/Sub) used to send the message without the token: the payload was in
/// the store, the wire body was empty, and the receiver failed to deserialize it. Every built-in mapper
/// derives from <see cref="EnvelopeMapper{TIncoming,TOutgoing}" />, which now serializes the body before any
/// header is mapped.
/// </summary>
public class claim_check_headers_reach_the_wire_5889
{
    private static ClaimCheckMessageSerializer claimCheck(IClaimCheckStore store, long? threshold = null)
    {
        var inner = new SystemTextJsonSerializer(SystemTextJsonSerializer.DefaultOptions());
        return new ClaimCheckMessageSerializer(inner, store, threshold);
    }

    private static Envelope outgoing(object message, IMessageSerializer serializer)
    {
        return new Envelope(message)
        {
            Serializer = serializer,
            ContentType = serializer.ContentType,
            Destination = new Uri("stub://one")
        };
    }

    [Fact]
    public void an_auto_offloaded_body_carries_its_token_onto_the_outgoing_message()
    {
        var store = new RecordingInMemoryClaimCheckStore();
        var envelope = outgoing(new PlainMessage("big", new byte[4096]), claimCheck(store, threshold: 1024));

        var message = new StubTransportMessage();
        new StubEnvelopeMapper(new StubEndpoint()).MapEnvelopeToOutgoing(envelope, message);

        message.Headers.Keys.ShouldContain(ClaimCheckHeaders.BodyHeaderName);
        message.Headers[ClaimCheckHeaders.BodyHeaderName].ShouldBe(envelope.Headers[ClaimCheckHeaders.BodyHeaderName]);

        // The body went to the store and the wire body is the empty placeholder the header stands in for
        store.StoreCount.ShouldBe(1);
        envelope.Data.ShouldBeEmpty();
    }

    [Fact]
    public void a_blob_property_carries_its_token_onto_the_outgoing_message()
    {
        var store = new RecordingInMemoryClaimCheckStore();
        var envelope = outgoing(new BlobByteArrayMessage("doc", [1, 2, 3, 4]), claimCheck(store));

        var message = new StubTransportMessage();
        new StubEnvelopeMapper(new StubEndpoint()).MapEnvelopeToOutgoing(envelope, message);

        var header = BlobTypeInfo.For(typeof(BlobByteArrayMessage)).Properties.Single().HeaderName;
        message.Headers.Keys.ShouldContain(header);
        store.StoreCount.ShouldBe(1);
    }

    [Fact]
    public void the_body_is_serialized_once()
    {
        var store = new RecordingInMemoryClaimCheckStore();
        var envelope = outgoing(new PlainMessage("big", new byte[4096]), claimCheck(store, threshold: 1024));

        new StubEnvelopeMapper(new StubEndpoint()).MapEnvelopeToOutgoing(envelope, new StubTransportMessage());

        // The sender reads the body after mapping, as RabbitMqSender does. That must not off-load it again
        _ = envelope.Data;
        _ = envelope.Data;

        store.StoreCount.ShouldBe(1);
    }

    [Fact]
    public void an_already_serialized_envelope_is_not_serialized_again()
    {
        var serializer = new CountingSerializer();
        var envelope = outgoing(new PlainMessage("small", [1]), serializer);
        envelope.Data = [1, 2, 3];

        new StubEnvelopeMapper(new StubEndpoint()).MapEnvelopeToOutgoing(envelope, new StubTransportMessage());

        serializer.Writes.ShouldBe(0);
        envelope.Data.ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public void an_envelope_without_a_serializer_is_left_to_the_sender_as_before()
    {
        // Nothing to serialize with: mapping the headers must not throw on the sender's behalf
        var envelope = new Envelope(new PlainMessage("x", [1])) { Destination = new Uri("stub://one") };

        var message = new StubTransportMessage();
        new StubEnvelopeMapper(new StubEndpoint()).MapEnvelopeToOutgoing(envelope, message);

        message.Headers.Keys.ShouldContain(EnvelopeConstants.IdKey);
    }

    [Fact]
    public void an_encrypted_body_carries_its_key_id_onto_the_outgoing_message()
    {
        // The same bug class: the encrypting serializer stamps the key id while it serializes
        var keys = new InMemoryKeyProvider("k1", new Dictionary<string, byte[]> { ["k1"] = new byte[32] });
        var inner = new SystemTextJsonSerializer(SystemTextJsonSerializer.DefaultOptions());
        var envelope = outgoing(new PlainMessage("secret", [1, 2, 3]), new EncryptingMessageSerializer(inner, keys));

        var message = new StubTransportMessage();
        new StubEnvelopeMapper(new StubEndpoint()).MapEnvelopeToOutgoing(envelope, message);

        message.Headers.Keys.ShouldContain(EncryptionHeaders.KeyIdHeader);
        message.Headers[EncryptionHeaders.KeyIdHeader].ShouldBe("k1");
    }

    private class CountingSerializer : IMessageSerializer
    {
        public int Writes;
        public string ContentType => "application/json";

        public byte[] Write(Envelope envelope)
        {
            Writes++;
            return [9];
        }

        public object ReadFromData(Type messageType, Envelope envelope) => throw new NotSupportedException();
        public object ReadFromData(byte[] data) => throw new NotSupportedException();
        public byte[] WriteMessage(object message) => throw new NotSupportedException();
    }
}
