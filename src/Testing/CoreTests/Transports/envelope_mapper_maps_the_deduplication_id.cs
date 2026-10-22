using Shouldly;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Transports;

/// <summary>
///     D2 (durable compliance findings, 2026-10-22). <see cref="Envelope.DeduplicationId" /> rode only the
///     <see cref="EnvelopeSerializer" /> wire format, so it survived SQS, database queues and local queues (all of
///     which carry the whole serialized envelope) but not RabbitMQ or any other broker built on the generic
///     <see cref="EnvelopeMapper{TIncoming,TOutgoing}" />. Every copy of a <c>[Deduplicated]</c> message arrived
///     without its id and was dead-lettered with <c>MissingDeduplicationIdException</c>.
/// </summary>
public class envelope_mapper_maps_the_deduplication_id
{
    [Fact]
    public void writes_the_deduplication_id_header()
    {
        var outgoing = new Envelope { DeduplicationId = "invoice-17|2026-10-22" };
        var written = new StubTransportMessage();

        new StubEnvelopeMapper(new StubEndpoint()).MapEnvelopeToOutgoing(outgoing, written);

        written.Headers[EnvelopeConstants.DeduplicationIdKey].ShouldBe("invoice-17|2026-10-22");
    }

    [Fact]
    public void reads_the_deduplication_id_header()
    {
        var incoming = new StubTransportMessage();
        incoming.Headers[EnvelopeConstants.DeduplicationIdKey] = "invoice-17|2026-10-22";

        var envelope = new Envelope();
        new StubEnvelopeMapper(new StubEndpoint()).MapIncomingToEnvelope(envelope, incoming);

        envelope.DeduplicationId.ShouldBe("invoice-17|2026-10-22");
    }

    [Fact]
    public void round_trips_the_deduplication_id()
    {
        var mapper = new StubEnvelopeMapper(new StubEndpoint());
        var wire = new StubTransportMessage();

        mapper.MapEnvelopeToOutgoing(new Envelope { DeduplicationId = "José|Abc" }, wire);

        var received = new Envelope();
        mapper.MapIncomingToEnvelope(received, wire);

        received.DeduplicationId.ShouldBe("José|Abc");
    }

    [Fact]
    public void writes_nothing_when_there_is_no_deduplication_id()
    {
        var written = new StubTransportMessage();

        new StubEnvelopeMapper(new StubEndpoint()).MapEnvelopeToOutgoing(new Envelope(), written);

        written.Headers.ContainsKey(EnvelopeConstants.DeduplicationIdKey).ShouldBeFalse();
    }
}
