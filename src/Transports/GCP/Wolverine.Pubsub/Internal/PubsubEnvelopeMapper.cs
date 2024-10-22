using System.Text.RegularExpressions;
using Google.Cloud.PubSub.V1;
using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.Pubsub.Internal;

internal class PubsubEnvelopeMapper : EnvelopeMapper<PubsubMessage, PubsubMessage>, IPubsubEnvelopeMapper {
    // private const string _wlvrnPrefix = "wlvrn";
    // private static Regex _wlvrnRegex = new Regex($"^{_wlvrnPrefix}\\.");

    public PubsubEnvelopeMapper(Endpoint endpoint) : base(endpoint) {
        MapProperty(
            e => e.CorrelationId!,
            (e, m) => { },
            (e, m) => m.OrderingKey = e.GroupId ?? string.Empty
        );
        MapProperty(
            x => x.Id,
            (e, m) => {
                if (!m.Attributes.TryGetValue("id", out var wolverineId)) return;
                if (!Guid.TryParse(wolverineId, out var id)) return;

                e.Id = id;
            },
            (e, m) => m.Attributes["id"] = e.Id.ToString()
        );
        MapProperty(
            e => e.MessageType!,
            (e, m) => {
                if (!m.Attributes.TryGetValue("message-type", out var messageType)) return;

                e.MessageType = messageType;
            },
            (e, m) => {
                if (e.MessageType is null) return;

                m.Attributes["message-type"] = e.MessageType;
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
