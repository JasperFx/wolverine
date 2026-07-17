using System.Text;
using Confluent.Kafka;
using Shouldly;
using Wolverine.Kafka.Internals;

namespace Wolverine.Kafka.Tests.Bugs;

// Regression for https://github.com/JasperFx/wolverine/issues/2838.
// JsonOnlyMapper used to unconditionally stamp _messageTypeName on every incoming
// message, so the wolverine-ping envelope emitted by InlineKafkaSender.PingAsync
// was routed to the user's handler as a Msg and System.Text.Json blew up on the
// 0x01 first byte of the ping body.
public class Bug_2838_receive_raw_json_honors_wolverine_ping
{
    [Fact]
    public void wolverine_ping_header_is_honored_by_json_only_mapper()
    {
        var topic = new KafkaTopic(
            new KafkaTransport(),
            "ping-regression",
            Wolverine.Configuration.EndpointRole.Application)
        {
            MessageType = typeof(Msg2838)
        };

        var mapper = new JsonOnlyMapper(topic);

        var incoming = new Message<string, byte[]>
        {
            Value = [1, 2, 3, 4], // mirrors Envelope.ForPing's body bytes
            Headers = new Headers
            {
                { EnvelopeConstants.MessageTypeKey, Encoding.UTF8.GetBytes(Envelope.PingMessageType) }
            }
        };

        var envelope = new Envelope();
        mapper.MapIncomingToEnvelope(envelope, incoming);

        envelope.MessageType.ShouldBe(Envelope.PingMessageType);
        envelope.IsPing().ShouldBeTrue();
    }

    [Fact]
    public void non_ping_payload_still_uses_default_message_type()
    {
        var topic = new KafkaTopic(
            new KafkaTransport(),
            "json-regression",
            Wolverine.Configuration.EndpointRole.Application)
        {
            MessageType = typeof(Msg2838)
        };

        var mapper = new JsonOnlyMapper(topic);

        var payload = Encoding.UTF8.GetBytes("{\"name\":\"hi\"}");
        var incoming = new Message<string, byte[]>
        {
            Value = payload
            // No headers — external producer.
        };

        var envelope = new Envelope();
        mapper.MapIncomingToEnvelope(envelope, incoming);

        envelope.IsPing().ShouldBeFalse();
        envelope.MessageType.ShouldNotBe(Envelope.PingMessageType);
        envelope.Data.ShouldBe(payload);
    }

    public record Msg2838(string Name);
}
