using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using JasperFx.Core;
using Wolverine.Transports;

namespace Wolverine.Pubsub.Internal;

internal class PubsubEnvelopeMapper : EnvelopeMapper<ReceivedMessage, PubsubMessage>, IPubsubEnvelopeMapper
{
    public PubsubEnvelopeMapper(PubsubEndpoint endpoint) : base(endpoint)
    {
        MapProperty(
            x => x.Id,
            (e, m) =>
            {
                if (!m.Message.Attributes.TryGetValue("id", out var wolverineId))
                {
                    return;
                }

                if (!Guid.TryParse(wolverineId, out var id))
                {
                    return;
                }

                e.Id = id;
            },
            (e, m) => m.Attributes["id"] = e.Id.ToString()
        );
        MapProperty(
            e => e.CorrelationId!,
            (e, m) => { },
            (e, m) =>
            {
                if (e.CorrelationId.IsEmpty())
                {
                    return;
                }

                m.OrderingKey = e.CorrelationId;
            }
        );
        MapProperty(
            e => e.MessageType!,
            (e, m) =>
            {
                if (!m.Message.Attributes.TryGetValue("message-type", out var messageType))
                {
                    return;
                }

                e.MessageType = messageType;
            },
            (e, m) =>
            {
                if (e.MessageType.IsEmpty())
                {
                    return;
                }

                m.Attributes["message-type"] = e.MessageType;
            }
        );
        MapProperty(
            e => e.Data!,
            (e, m) =>
            {
                if (m.Message.Data.IsEmpty)
                {
                    return;
                }

                e.Data = m.Message.Data.ToByteArray();
            },
            (e, m) =>
            {
                if (e.Data.IsNullOrEmpty())
                {
                    return;
                }

                m.Data = ByteString.CopyFrom(e.Data);
            }
        );
        MapProperty(
            e => e.Attempts,
            (e, m) =>
            {
                if (!m.Message.Attributes.TryGetValue("attempts", out var attempts))
                {
                    return;
                }

                if (!int.TryParse(attempts, out var count))
                {
                    return;
                }

                e.Attempts = count;
            },
            (e, m) => m.Attributes["attempts"] = e.Attempts.ToString()
        );
        MapProperty(
            e => e.ContentType!,
            (e, m) =>
            {
                if (!m.Message.Attributes.TryGetValue("content-type", out var contentType))
                {
                    return;
                }

                e.ContentType = contentType;
            },
            (e, m) =>
            {
                if (e.ContentType.IsEmpty())
                {
                    return;
                }

                m.Attributes["content-type"] = e.ContentType;
            }
        );
        MapProperty(
            e => e.Destination!,
            (e, m) =>
            {
                if (!m.Message.Attributes.TryGetValue("destination", out var destination))
                {
                    return;
                }

                if (!Uri.TryCreate(destination, UriKind.Absolute, out var uri))
                {
                    return;
                }

                e.Destination = uri;
            },
            (e, m) =>
            {
                if (e.Destination is null)
                {
                    return;
                }

                m.Attributes["destination"] = e.Destination.ToString();
            }
        );
        MapProperty(
            e => e.TenantId!,
            (e, m) =>
            {
                if (!m.Message.Attributes.TryGetValue("tenant-id", out var tenantId))
                {
                    return;
                }

                e.TenantId = tenantId;
            },
            (e, m) =>
            {
                if (e.TenantId.IsEmpty())
                {
                    return;
                }

                m.Attributes["tenant-id"] = e.TenantId;
            }
        );
        MapProperty(
            e => e.AcceptedContentTypes,
            (e, m) =>
            {
                if (!m.Message.Attributes.TryGetValue("accepted-content-types", out var acceptedContentTypes))
                {
                    return;
                }

                e.AcceptedContentTypes = acceptedContentTypes.Split(',');
            },
            (e, m) =>
            {
                if (e.AcceptedContentTypes.IsNullOrEmpty())
                {
                    return;
                }

                m.Attributes["accepted-content-types"] = string.Join(",",
                    e.AcceptedContentTypes.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
            }
        );
        MapProperty(
            e => e.TopicName!,
            (e, m) =>
            {
                if (!m.Message.Attributes.TryGetValue("topic-name", out var topicName))
                {
                    return;
                }

                e.TopicName = topicName;
            },
            (e, m) =>
            {
                if (e.TopicName.IsEmpty())
                {
                    return;
                }

                m.Attributes["topic-name"] = e.TopicName;
            }
        );
        MapProperty(
            e => e.EndpointName!,
            (e, m) =>
            {
                if (!m.Message.Attributes.TryGetValue("endpoint-name", out var endpointName))
                {
                    return;
                }

                e.EndpointName = endpointName;
            },
            (e, m) =>
            {
                if (e.EndpointName.IsEmpty())
                {
                    return;
                }

                m.Attributes["endpoint-name"] = e.EndpointName;
            }
        );
        MapProperty(
            e => e.WasPersistedInOutbox,
            (e, m) =>
            {
                if (!m.Message.Attributes.Keys.Contains("was-persisted-in-outbox"))
                {
                    return;
                }

                e.WasPersistedInOutbox = true;
            },
            (e, m) =>
            {
                if (!e.WasPersistedInOutbox)
                {
                    return;
                }

                m.Attributes["was-persisted-in-outbox"] = string.Empty;
            }
        );
        MapProperty(
            e => e.GroupId!,
            (e, m) =>
            {
                if (!m.Message.Attributes.TryGetValue("group-id", out var groupId))
                {
                    return;
                }

                e.GroupId = groupId;
            },
            (e, m) =>
            {
                if (e.GroupId.IsEmpty())
                {
                    return;
                }

                m.Attributes["group-id"] = e.GroupId;
            }
        );
        MapProperty(
            e => e.DeduplicationId!,
            (e, m) =>
            {
                if (!m.Message.Attributes.TryGetValue("deduplication-id", out var deduplicationId))
                {
                    return;
                }

                e.DeduplicationId = deduplicationId;
            },
            (e, m) =>
            {
                if (e.DeduplicationId.IsEmpty())
                {
                    return;
                }

                m.Attributes["deduplication-id"] = e.DeduplicationId;
            }
        );
        MapProperty(
            e => e.PartitionKey!,
            (e, m) =>
            {
                if (!m.Message.Attributes.TryGetValue("partition-key", out var partitionKey))
                {
                    return;
                }

                e.PartitionKey = partitionKey;
            },
            (e, m) =>
            {
                if (e.PartitionKey.IsEmpty())
                {
                    return;
                }

                m.Attributes["partition-key"] = e.PartitionKey;
            }
        );
    }

    public void MapIncomingToEnvelope(PubsubEnvelope envelope, ReceivedMessage incoming)
    {
        envelope.AckId = incoming.AckId;

        base.MapIncomingToEnvelope(envelope, incoming);
    }

    public void MapOutgoingToMessage(OutgoingMessageBatch outgoing, PubsubMessage message)
    {
        message.Data = ByteString.CopyFrom(outgoing.Data);
        message.Attributes["destination"] = outgoing.Destination.ToString();
        message.Attributes["batched"] = string.Empty;
    }

    protected override void writeOutgoingHeader(PubsubMessage outgoing, string key, string value)
    {
        outgoing.Attributes[key] = value;
    }

    protected override void writeIncomingHeaders(ReceivedMessage incoming, Envelope envelope)
    {
        if (incoming.Message.Attributes is null)
        {
            return;
        }

        foreach (var pair in incoming.Message.Attributes) envelope.Headers[pair.Key] = pair.Value;
    }

    protected override bool tryReadIncomingHeader(ReceivedMessage incoming, string key, out string? value)
    {
        if (incoming.Message.Attributes.TryGetValue(key, out var header))
        {
            value = header;

            return true;
        }

        value = null;

        return false;
    }
}