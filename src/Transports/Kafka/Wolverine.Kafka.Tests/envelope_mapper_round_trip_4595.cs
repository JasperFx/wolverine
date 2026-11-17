using Shouldly;
using Wolverine.Configuration;
using Wolverine.Kafka.Internals;
using Wolverine.Transports;

namespace Wolverine.Kafka.Tests;

/// <summary>
/// GH-4595: Kafka has the same round trip as Redis -- writeIncomingHeaders copies every wire header
/// into Envelope.Headers, and Kafka's outgoing headers are a list that happily accepts the same name
/// twice. No broker required.
/// </summary>
public class envelope_mapper_round_trip_4595
{
    private readonly KafkaEnvelopeMapper theMapper =
        new(new KafkaTopic(new KafkaTransport(), "round-trip-4595", EndpointRole.Application));

    [Fact]
    public void writes_the_protocol_version_exactly_once_when_re_sending_a_received_envelope()
    {
        var resent = mapOutgoing(receive(new Envelope { Data = "{}"u8.ToArray(), MessageType = "scratch" }));

        resent.Headers.Count(x => x.Key == TransportConstants.ProtocolVersion).ShouldBe(1);
    }

    [Fact]
    public void the_protocol_version_does_not_survive_as_an_envelope_header()
    {
        var received = receive(new Envelope { Data = "{}"u8.ToArray(), MessageType = "scratch" });

        received.Headers.ContainsKey(TransportConstants.ProtocolVersion).ShouldBeFalse();
    }

    private Envelope receive(Envelope outgoing)
    {
        var record = mapOutgoing(outgoing);
        var incoming = TestRecords.Incoming("round-trip-4595", record.Key, record.Value,
            record.Headers.Select(h =>
                new KeyValuePair<string, byte[]?>(h.Key, h.IsValueNull ? null : h.GetValueAsArray())));

        var received = new Envelope();
        theMapper.MapIncomingToEnvelope(received, incoming);

        return received;
    }

    private OutgoingKafkaRecord mapOutgoing(Envelope envelope)
    {
        var record = new OutgoingKafkaRecord();
        theMapper.MapEnvelopeToOutgoing(envelope, record);

        return record;
    }
}
