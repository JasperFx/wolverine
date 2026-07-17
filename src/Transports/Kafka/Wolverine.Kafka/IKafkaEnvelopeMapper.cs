using System.Text;
using System.Text.Json;
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
    private readonly JsonSerializerOptions _options;
    private readonly string? _messageTypeName;

    public JsonOnlyMapper(KafkaTopic topic, JsonSerializerOptions options)
    {
        _options = options;

        _messageTypeName = topic.MessageType?.ToMessageTypeName();
    }

    public void MapEnvelopeToOutgoing(Envelope envelope, Message<string, byte[]> outgoing)
    {
        outgoing.Key = envelope.GroupId!;

        if (envelope.Data != null && envelope.Data.Any())
        {
            outgoing.Value = envelope.Data;
        }
        else if (envelope.Message != null)
        {
            outgoing.Value = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope.Message, _options));
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