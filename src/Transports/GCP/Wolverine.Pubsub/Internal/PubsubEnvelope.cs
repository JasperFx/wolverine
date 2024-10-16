namespace Wolverine.Pubsub.Internal;

public class PubsubEnvelope : Envelope {
    public string AckId { get; set; } = string.Empty;

    public PubsubEnvelope(string ackId) {
        AckId = ackId;
    }
}
