using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.Pubsub.Internal;

internal class PubsubEnvelopeMapper : EnvelopeMapper<PubsubMessage, PubsubMessage>, IPubsubEnvelopeMapper {
    // private const string _wlvrnPrefix = "wlvrn";
    // private static Regex _wlvrnRegex = new Regex($"^{_wlvrnPrefix}\\.");

    public PubsubEnvelopeMapper(Endpoint endpoint) : base(endpoint) {
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
            e => e.CorrelationId!,
            (e, m) => { },
            (e, m) => m.OrderingKey = e.GroupId ?? string.Empty
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
        MapProperty(
            e => e.Data!,
            (e, m) => {
                if (m.Data is null) return;

                e.Data = m.Data.ToByteArray();
            },
            (e, m) => m.Data = ByteString.CopyFrom(e.Data)
        );
        MapProperty(
            e => e.Attempts,
            (e, m) => {
                if (!m.Attributes.TryGetValue("attempts", out var attempts)) return;
                if (!int.TryParse(attempts, out var count)) return;

                e.Attempts = count;
            },
            (e, m) => m.Attributes["attempts"] = e.Attempts.ToString()
        );
        MapProperty(
            e => e.ContentType!,
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
            e => e.Destination!,
            (e, m) => {
                if (!m.Attributes.TryGetValue("destination", out var destination)) return;
                if (!Uri.TryCreate(destination, UriKind.Absolute, out var uri)) return;

                e.Destination = uri;
            },
            (e, m) => {
                if (e.Destination is null) return;

                m.Attributes["destination"] = e.Destination.ToString();
            }
        );
        MapProperty(
            e => e.TenantId!,
            (e, m) => {
                if (!m.Attributes.TryGetValue("tenant-id", out var tenantId)) return;

                e.TenantId = tenantId;
            },
            (e, m) => {
                if (e.TenantId is null) return;

                m.Attributes["tenant-id"] = e.TenantId;
            }
        );
        MapProperty(
            e => e.AcceptedContentTypes,
            (e, m) => {
                if (!m.Attributes.TryGetValue("accepted-content-types", out var acceptedContentTypes)) return;

                e.AcceptedContentTypes = acceptedContentTypes.Split(',');
            },
            (e, m) => {
                if (e.AcceptedContentTypes is null) return;

                m.Attributes["accepted-content-types"] = string.Join(",", e.AcceptedContentTypes.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
            }
        );
        MapProperty(
            e => e.TopicName!,
            (e, m) => {
                if (!m.Attributes.TryGetValue("topic-name", out var topicName)) return;

                e.TopicName = topicName;
            },
            (e, m) => {
                if (e.TopicName is null) return;

                m.Attributes["topic-name"] = e.TopicName;
            }
        );
        MapProperty(
            e => e.EndpointName!,
            (e, m) => {
                if (!m.Attributes.TryGetValue("endpoint-name", out var endpointName)) return;

                e.EndpointName = endpointName;
            },
            (e, m) => {
                if (e.EndpointName is null) return;

                m.Attributes["endpoint-name"] = e.EndpointName;
            }
        );
        MapProperty(
            e => e.WasPersistedInOutbox,
            (e, m) => {
                if (!m.Attributes.TryGetValue("was-persisted-in-outbox", out var wasPersistedInOutbox)) return;
                if (!bool.TryParse(wasPersistedInOutbox, out var wasPersisted)) return;

                e.WasPersistedInOutbox = wasPersisted;
            },
            (e, m) => m.Attributes["was-persisted-in-outbox"] = e.WasPersistedInOutbox.ToString()
        );
        MapProperty(
            e => e.GroupId!,
            (e, m) => {
                if (!m.Attributes.TryGetValue("group-id", out var groupId)) return;

                e.GroupId = groupId;
            },
            (e, m) => {
                if (e.GroupId is null) return;

                m.Attributes["group-id"] = e.GroupId;
            }
        );
        MapProperty(
            e => e.DeduplicationId!,
            (e, m) => {
                if (!m.Attributes.TryGetValue("deduplication-id", out var deduplicationId)) return;

                e.DeduplicationId = deduplicationId;
            },
            (e, m) => {
                if (e.DeduplicationId is null) return;

                m.Attributes["deduplication-id"] = e.DeduplicationId;
            }
        );
        MapProperty(
            e => e.PartitionKey!,
            (e, m) => {
                if (!m.Attributes.TryGetValue("partition-key", out var partitionKey)) return;

                e.PartitionKey = partitionKey;
            },
            (e, m) => {
                if (e.PartitionKey is null) return;

                m.Attributes["partition-key"] = e.PartitionKey;
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
