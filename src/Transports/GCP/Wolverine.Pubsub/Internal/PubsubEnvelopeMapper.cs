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
        
        MapPropertyToHeader(x => x.GroupId, "group-id");
        MapPropertyToHeader(x => x.DeduplicationId, "deduplication-id");
        MapPropertyToHeader(x => x.PartitionKey, "partition-key");
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
        message.Attributes["batched"] = "1";
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