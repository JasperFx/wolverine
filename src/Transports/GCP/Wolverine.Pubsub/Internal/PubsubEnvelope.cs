using Google.Cloud.PubSub.V1;

namespace Wolverine.Pubsub.Internal;

public class PubsubEnvelope : Envelope
{
    public string AckId { get; set; } = string.Empty;
    public SubscriberClient.Reply Reply { get; set; } = SubscriberClient.Reply.Ack;
}