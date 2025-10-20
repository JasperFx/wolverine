using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using ImTools;
using Wolverine.Transports;

namespace Wolverine.Pubsub.Internal;

/// <summary>
///     Pluggable strategy for reading and writing data to Google Cloud Platform Pub/Sub
/// </summary>
public interface IPubsubEnvelopeMapper : IEnvelopeMapper<PubsubMessage, PubsubMessage>
{

}

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

    protected override void writeOutgoingHeader(PubsubMessage outgoing, string key, string value)
    {
        outgoing.Attributes[key] = value;
    }

    protected override bool tryReadIncomingHeader(PubsubMessage incoming, string key, out string? value)
    {
        if (incoming.Attributes == null)
        {
            value = default;
            return false;
        }
        
        if (incoming.Attributes.TryGetValue(key, out var header))
        {
            value = header;

            return true;
        }

        value = null;

        return false;
    }

    protected override void writeIncomingHeaders(PubsubMessage incoming, Envelope envelope)
    {
        if (incoming.Attributes is null)
        {
            return;
        }

        foreach (var pair in incoming.Attributes) envelope.Headers[pair.Key] = pair.Value;
    }
}