using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using JasperFx.Core;
using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.Pubsub.Internal;

internal class PubsubEnvelopeMapper : EnvelopeMapper<PubsubMessage, PubsubMessage>, IPubsubEnvelopeMapper {
    public PubsubEnvelopeMapper(Endpoint endpoint) : base(endpoint) {
        MapProperty(
            x => x.Data!,
            (e, m) => e.Data = m.Data.ToByteArray(),
            (e, m) => {
                if (e.Data is null) return;

                m.Data = ByteString.CopyFrom(e.Data);
            }
        );
        MapProperty(
            x => x.ContentType!,
            (e, m) => {
                if (!m.Attributes.TryGetValue("content-type", out var contentType)) return;

                e.ContentType = contentType;
            },
            (e, m) => {
                if (e.ContentType is null) return;

                m.Attributes["content-type"] = e.ContentType;
            }
        );
        MapProperty(
            x => x.GroupId!,
            (e, m) => e.GroupId = m.OrderingKey.IsNotEmpty() ? m.OrderingKey : null,
            (e, m) => m.OrderingKey = e.GroupId ?? string.Empty
        );
        MapProperty(
            x => x.Id,
            (e, m) => {
                if (!m.Attributes.TryGetValue("wolverine-id", out var wolverineId)) return;
                if (!Guid.TryParse(wolverineId, out var id)) return;

                e.Id = id;
            },
            (e, m) => m.Attributes["wolverine-id"] = e.Id.ToString()
        );
        MapProperty(
            x => x.CorrelationId!,
            (e, m) => {
                if (!m.Attributes.TryGetValue("wolverine-correlation-id", out var correlationId)) return;

                e.CorrelationId = correlationId;
            },
            (e, m) => {
                if (e.CorrelationId is null) return;

                m.Attributes["wolverine-correlation-id"] = e.CorrelationId;
            }
        );
        MapProperty(
            x => x.MessageType!,
            (e, m) => {
                if (!m.Attributes.TryGetValue("wolverine-message-type", out var messageType)) return;

                e.MessageType = messageType;
            },
            (e, m) => {
                if (e.MessageType is null) return;

                m.Attributes["wolverine-message-type"] = e.MessageType;
            }
        );
    }

    protected override void writeOutgoingHeader(PubsubMessage outgoing, string key, string value) {
        outgoing.Attributes[key] = value;
    }

    protected override void writeIncomingHeaders(PubsubMessage incoming, Envelope envelope) {
        if (incoming.Attributes is null) return;

        foreach (var pair in incoming.Attributes) envelope.Headers[pair.Key] = pair.Value?.ToString();
    }

    protected override bool tryReadIncomingHeader(PubsubMessage incoming, string key, out string? value) {
        if (incoming.Attributes.TryGetValue(key, out var header)) {
            value = header.ToString();

            return true;
        }

        value = null;

        return false;
    }
}
