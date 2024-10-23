using Google.Cloud.PubSub.V1;
using Wolverine.Transports;

namespace Wolverine.Pubsub;

/// <summary>
/// Pluggable strategy for reading and writing data to Google Cloud Pub/Sub
/// </summary>
public interface IPubsubEnvelopeMapper : IEnvelopeMapper<ReceivedMessage, PubsubMessage> {
    void MapIncomingToEnvelope(PubsubEnvelope envelope, ReceivedMessage incoming);
    void MapOutgoingToMessage(OutgoingMessageBatch outgoing, PubsubMessage message);
};
