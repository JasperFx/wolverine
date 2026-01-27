using Google.Cloud.PubSub.V1;
using Wolverine.Transports;

namespace Wolverine.Pubsub;

/// <summary>
///     Pluggable strategy for reading and writing data to Google Cloud Platform Pub/Sub
/// </summary>
public interface IPubsubEnvelopeMapper : IEnvelopeMapper<PubsubMessage, PubsubMessage>
{
    void MapOutgoingToMessage(OutgoingMessageBatch outgoing, PubsubMessage message);
}