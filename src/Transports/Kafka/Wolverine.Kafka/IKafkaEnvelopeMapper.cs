using System.Text;
using Confluent.Kafka;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Util;

namespace Wolverine.Kafka;

public interface IKafkaEnvelopeMapper : IEnvelopeMapper<Message<string, byte[]>, Message<string, byte[]>>;

/// <summary>
/// Option to publish or receive raw JSON from or to Kafka topics
/// </summary>
internal class JsonOnlyMapper : IKafkaEnvelopeMapper
{
    private readonly KafkaTopic _topic;
    private readonly string? _messageTypeName;

    public JsonOnlyMapper(KafkaTopic topic)
    {
        _topic = topic;
        _messageTypeName = topic.MessageType?.ToMessageTypeName();
    }

    public void MapEnvelopeToOutgoing(Envelope envelope, Message<string, byte[]> outgoing)
    {
        outgoing.Key = envelope.GroupId!;

        // Sender liveness pings must stay recognizable on the receiving side. A raw-JSON
        // listener identifies pings solely by the message-type header (see the guard in
        // MapIncomingToEnvelope / GH-2838), so that one header still has to ride along even
        // though this mapper strips all other Wolverine metadata off the record.
        if (envelope.MessageType == Envelope.PingMessageType)
        {
            outgoing.Headers ??= new Headers();
            outgoing.Headers.Add(EnvelopeConstants.MessageTypeKey, Encoding.UTF8.GetBytes(Envelope.PingMessageType));
            outgoing.Value = envelope.Data ?? [];
            return;
        }

        // Envelope.Data lazily serializes the message through the endpoint's serializer,
        // so this covers both pre-serialized (durable/forwarded) and live-message sends.
        if (envelope.Data != null && envelope.Data.Any())
        {
            outgoing.Value = envelope.Data;
        }
        else
        {
            throw new InvalidOperationException(
                $"Envelope {envelope.Id} has neither {nameof(envelope.Data)} nor {nameof(Envelope.Message)}");
        }
    }

    public void MapIncomingToEnvelope(Envelope envelope, Message<string, byte[]> incoming)
    {
        // Honor Wolverine's own infrastructure messages (currently just ping) even when this
        // listener is configured for raw JSON. InlineKafkaSender.PingAsync produces a
        // wolverine-ping envelope on the topic for sender liveness checks; without this guard
        // the raw-JSON mapper would stamp the user's message type on it, and the four-byte
        // ping body would blow up the JSON deserializer with "0x01 is an invalid start of a
        // value." See https://github.com/JasperFx/wolverine/issues/2838.
        if (TryReadHeader(incoming, EnvelopeConstants.MessageTypeKey, out var incomingMessageType)
            && incomingMessageType == Envelope.PingMessageType)
        {
            envelope.MessageType = Envelope.PingMessageType;
            envelope.Data = incoming.Value;
            return;
        }

        envelope.Data = incoming.Value;
        envelope.MessageType = _messageTypeName;

        // Raw records carry no content-type, which routes deserialization to the *global*
        // default serializer (HandlerPipeline.serializerFor) — stamping the endpoint's own
        // serializer here is what lets ReceiveRawJson's JsonSerializerOptions take effect.
        // ContentType is stamped too so a durable-inbox recovery replay, which loses the
        // transient Serializer reference, still resolves this endpoint's serializer through
        // the content-type lookup.
        if (_topic.DefaultSerializer != null)
        {
            envelope.Serializer = _topic.DefaultSerializer;
            envelope.ContentType = _topic.DefaultSerializer.ContentType;
        }

        if (incoming.Timestamp.Type is TimestampType.CreateTime or TimestampType.LogAppendTime)
        {
            envelope.SentAt = incoming.Timestamp.UtcDateTime;
        }

        if (incoming.Headers is null)
        {
            return;
        }

        foreach (var header in incoming.Headers)
        {
            if (EnvelopeSerializer.ReservedHeaderKeys.Contains(header.Key))
            {
                continue;
            }

            if (TryReadHeader(incoming, header.Key, out var headerValue))
            {
                envelope.Headers.TryAdd(header.Key, headerValue);
            }
        }
    }

    private static bool TryReadHeader(Message<string, byte[]> incoming, string key, out string? value)
    {
        if (incoming.Headers != null && incoming.Headers.TryGetLastBytes(key, out var bytes))
        {
            value = bytes is null ? null : Encoding.UTF8.GetString(bytes);
            return true;
        }

        value = null;
        return false;
    }
}
