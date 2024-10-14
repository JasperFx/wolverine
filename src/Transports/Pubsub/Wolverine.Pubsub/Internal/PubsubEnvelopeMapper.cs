using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.Pubsub.Internal;

internal class PubsubEnvelopeMapper : EnvelopeMapper<PubsubMessage, PubsubMessage>, IPubsubEnvelopeMapper {
    public PubsubEnvelopeMapper(Endpoint endpoint) : base(endpoint) {
        MapProperty(
            x => x.ContentType!,
            (e, m) => e.ContentType = m.Attributes["Content-Type"],
            (e, m) => {
                if (e.ContentType is null) return;

                m.Attributes["Content-Type"] = e.ContentType;
            }
        );
        MapProperty(
            x => x.Data!,
            (e, m) => e.Data = m.Data.ToByteArray(),
            (e, m) => {
                if (e.Data is null) return;

                m.Data = ByteString.CopyFrom(e.Data);
            }
        );
        MapProperty(
            x => x.Id,
            (e, m) => {
                if (Guid.TryParse(m.Attributes["Wolverine-Id"], out var id)) {
                    e.Id = id;
                }
            },
            (e, m) => m.Attributes["Wolverine-Id"] = e.Id.ToString()
        );
        MapProperty(
            x => x.CorrelationId!,
            (e, m) => e.CorrelationId = m.Attributes["Wolverine-Correlation-Id"],
            (e, m) => {
                if (e.CorrelationId is null) return;

                m.Attributes["Wolverine-Correlation-Id"] = e.CorrelationId;
            }
        );
        MapProperty(
            x => x.MessageType!,
            (e, m) => e.MessageType = m.Attributes["Wolverine-Message-Type"],
            (e, m) => {
                if (e.MessageType is null) return;

                m.Attributes["Wolverine-Message-Type"] = e.MessageType;
            }
        );
        MapProperty(
            x => x.GroupId!,
            (e, m) => e.GroupId = m.OrderingKey,
            (e, m) => m.OrderingKey = e.GroupId ?? string.Empty
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
