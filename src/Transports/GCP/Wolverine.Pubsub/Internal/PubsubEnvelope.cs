namespace Wolverine.Pubsub.Internal;

public class PubsubEnvelope : Envelope {
    public readonly string AckId;

    public PubsubEnvelope(string ackId) {
        AckId = ackId;
    }
}
