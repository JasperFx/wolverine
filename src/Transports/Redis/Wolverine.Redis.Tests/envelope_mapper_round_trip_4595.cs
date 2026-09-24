using Shouldly;
using StackExchange.Redis;
using Wolverine.Redis.Internal;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.Redis.Tests;

/// <summary>
/// GH-4595: re-sending an envelope that was received from a Redis stream wrote the protocol
/// version field twice, because writeIncomingHeaders copied it into Envelope.Headers and
/// MapEnvelopeToOutgoing stamps its own copy on every send. No broker required.
/// </summary>
public class envelope_mapper_round_trip_4595
{
    private const string ProtocolVersionField = "wolverine-wolverine-protocol-version";

    private readonly RedisEnvelopeMapper theMapper;

    public envelope_mapper_round_trip_4595()
    {
        var transport = new RedisTransport();
        theMapper = new RedisEnvelopeMapper(transport.StreamEndpoint("scratch-stream"));
    }

    [Fact]
    public void writes_the_protocol_version_exactly_once_on_a_first_send()
    {
        var fields = mapOutgoing(new Envelope { Data = "{}"u8.ToArray(), MessageType = "scratch" });

        fields.Count(x => x.Name == ProtocolVersionField).ShouldBe(1);
    }

    [Fact]
    public void writes_the_protocol_version_exactly_once_when_re_sending_a_received_envelope()
    {
        var resent = mapOutgoing(receive(new Envelope { Data = "{}"u8.ToArray(), MessageType = "scratch" }));

        resent.Count(x => x.Name == ProtocolVersionField).ShouldBe(1);
    }

    [Fact]
    public void the_protocol_version_does_not_survive_as_an_envelope_header()
    {
        var received = receive(new Envelope { Data = "{}"u8.ToArray(), MessageType = "scratch" });

        received.Headers.ContainsKey(TransportConstants.ProtocolVersion).ShouldBeFalse();
    }

    [Fact]
    public void a_dead_letter_entry_can_still_be_read_into_a_dictionary()
    {
        var entry = new StreamEntry("2-0",
            mapOutgoing(receive(new Envelope { Data = "{}"u8.ToArray(), MessageType = "scratch" })).ToArray());

        // This is what a consumer reading the native dead letter queue does, and what threw
        // "An item with the same key has already been added"
        Should.NotThrow(() => entry.Values.ToDictionary(x => x.Name.ToString(), x => x.Value.ToString()));
    }

    /// <summary>
    /// Map an envelope out exactly as a sender does, then read it back exactly as the listener does
    /// </summary>
    private Envelope receive(Envelope outgoing)
    {
        var received = new Envelope();
        theMapper.MapIncomingToEnvelope(received, new StreamEntry("1-0", mapOutgoing(outgoing).ToArray()));

        return received;
    }

    private List<NameValueEntry> mapOutgoing(Envelope envelope)
    {
        var fields = new List<NameValueEntry>();
        theMapper.MapEnvelopeToOutgoing(envelope, fields);

        return fields;
    }
}
