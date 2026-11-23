using NATS.Client.Core;
using Wolverine.Runtime.Serialization;
using Wolverine.Util;

namespace Wolverine.Nats.Internal;

/// <summary>
/// GH-5924. Plain JSON in and out of a NATS subject, for publishers and subscribers that are not Wolverine:
/// <c>ReceiveRawJson()</c> and <c>PublishRawJson()</c>. The same shape as the Kafka transport's raw JSON mapper.
/// </summary>
internal class JsonOnlyMapper : INatsEnvelopeMapper
{
    private readonly NatsEndpoint _endpoint;
    private readonly string? _messageTypeName;

    public JsonOnlyMapper(NatsEndpoint endpoint)
    {
        _endpoint = endpoint;
        _messageTypeName = endpoint.MessageType?.ToMessageTypeName();
    }

    public void MapEnvelopeToOutgoing(Envelope envelope, NatsHeaders outgoing)
    {
        // No Wolverine headers at all. The body is Envelope.Data, which the sender reads after mapping and which
        // the endpoint's serializer writes
    }

    public void MapIncomingToEnvelope(Envelope envelope, NatsMsg<byte[]> incoming)
    {
        envelope.Data = incoming.Data;
        envelope.MessageType = _messageTypeName;

        // The listener's own address, as with the built in mapper
        envelope.Destination = _endpoint.Uri;

        // A raw message carries no content-type, which would leave deserialization to the global default
        // serializer. The endpoint's own serializer is what lets ReceiveRawJson's JsonSerializerOptions apply, and the
        // content type finds it again for an envelope recovered from the durable inbox
        if (_endpoint.DefaultSerializer != null)
        {
            envelope.Serializer = _endpoint.DefaultSerializer;
            envelope.ContentType = _endpoint.DefaultSerializer.ContentType;
        }

        if (incoming.Headers == null)
        {
            return;
        }

        foreach (var header in incoming.Headers)
        {
            if (EnvelopeSerializer.ReservedHeaderKeys.Contains(header.Key))
            {
                continue;
            }

            envelope.Headers[header.Key] = header.Value;
        }
    }
}
