using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using ImTools;
using JasperFx.Core;
using Wolverine.Transports;

namespace Wolverine.Pubsub.Internal;

public class PubsubEnvelopeMapper : EnvelopeMapper<PubsubMessage, PubsubMessage>, IPubsubEnvelopeMapper
{
    public PubsubEnvelopeMapper(PubsubEndpoint endpoint) : base(endpoint)
    {
        MapProperty(
            e => e.Data!,
            (e, m) =>
            {
                if (m.Data.IsEmpty)
                {
                    return;
                }

                e.Data = m.Data.ToByteArray();
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

        MapPropertyToHeader(x => x.GroupId, "group-id");
        MapPropertyToHeader(x => x.DeduplicationId, "deduplication-id");
        MapPropertyToHeader(x => x.PartitionKey, "partition-key");
    }

    public void MapOutgoingToMessage(OutgoingMessageBatch outgoing, PubsubMessage message)
    {
        message.Data = ByteString.CopyFrom(outgoing.Data);
        message.Attributes["destination"] = outgoing.Destination.ToString();
        message.Attributes["batched"] = "1";
    }

    protected override void writeOutgoingHeader(PubsubMessage outgoing, string key, string value)
    {
        outgoing.Attributes[key] = value;
    }

    protected override void writeIncomingHeaders(PubsubMessage incoming, Envelope envelope)
    {
        if (incoming.Attributes is null)
        {
            return;
        }

        foreach (var pair in incoming.Attributes) envelope.Headers[pair.Key] = pair.Value;
    }

    protected override bool tryReadIncomingHeader(PubsubMessage incoming, string key, out string? value)
    {
        if (incoming.Attributes.TryGetValue(key, out var header))
        {
            value = header;

            return true;
        }

        value = null;

        return false;
    }
}