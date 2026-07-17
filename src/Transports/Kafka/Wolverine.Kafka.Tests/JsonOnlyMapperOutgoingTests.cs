using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Shouldly;
using Wolverine.Kafka.Internals;

namespace Wolverine.Kafka.Tests;

public class JsonOnlyMapperOutgoingTests
{
    [Fact]
    public void outgoing_message_carries_no_wolverine_headers()
    {
        // In the runtime sending path, Envelope.Data is already serialized by the endpoint's
        // serializer before the mapper runs, so the mapper's only job is the body + key —
        // no Wolverine protocol headers may leak onto the record.
        var envelope = new Envelope
        {
            Data = JsonSerializer.SerializeToUtf8Bytes(new ColorMessage("blue")),
            GroupId = "group-1",
            TenantId = "tenant-1",
            CorrelationId = "correlation-1"
        };
        var outgoing = new Message<string, byte[]>();

        buildMapper().MapEnvelopeToOutgoing(envelope, outgoing);

        (outgoing.Headers?.Any() ?? false).ShouldBeFalse();
        JsonSerializer.Deserialize<ColorMessage>(outgoing.Value)!.Color.ShouldBe("blue");
        outgoing.Key.ShouldBe("group-1");
    }

    [Fact]
    public void outgoing_ping_round_trips_through_raw_json_mappers()
    {
        // Sender liveness pings have to survive a raw-JSON topic where both ends use
        // JsonOnlyMapper. The receiving side recognizes pings only by the message-type
        // header (GH-2838), so the outgoing side must write that one header.
        var mapper = buildMapper();
        var ping = Envelope.ForPing(new Uri("kafka://localhost/pings"));
        var outgoing = new Message<string, byte[]>();

        mapper.MapEnvelopeToOutgoing(ping, outgoing);

        var received = new Envelope();
        mapper.MapIncomingToEnvelope(received, outgoing);

        received.MessageType.ShouldBe(Envelope.PingMessageType);
    }

    [Fact]
    public void outgoing_ping_writes_only_the_message_type_header()
    {
        var mapper = buildMapper();
        var ping = Envelope.ForPing(new Uri("kafka://localhost/pings"));
        var outgoing = new Message<string, byte[]>();

        mapper.MapEnvelopeToOutgoing(ping, outgoing);

        outgoing.Headers.Select(x => x.Key).Single().ShouldBe(EnvelopeConstants.MessageTypeKey);
        outgoing.Value.ShouldBe(ping.Data);
    }

    private static JsonOnlyMapper buildMapper()
    {
        var topic = new KafkaTopic(
            new KafkaTransport(),
            "outgoing-mapping",
            Configuration.EndpointRole.Application)
        {
            MessageType = typeof(ColorMessage)
        };

        return new JsonOnlyMapper(topic, new JsonSerializerOptions());
    }
}
