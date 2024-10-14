using Google.Cloud.PubSub.V1;

namespace Wolverine.Pubsub.Internal;

public class PubsubEnvelope : Envelope {
    public string AckId { get; set; } = string.Empty;
    public new PubsubMessage? Message => Message;

    public PubsubEnvelope(PubsubMessage message, string ackId) : base(message) {
        AckId = ackId;
    }
}
