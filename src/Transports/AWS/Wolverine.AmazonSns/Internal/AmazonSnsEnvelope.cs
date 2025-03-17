using Amazon.SimpleNotificationService.Util;

namespace Wolverine.AmazonSns.Internal;

internal class AmazonSnsEnvelope : Envelope
{
    public AmazonSnsEnvelope(Message snsMessage)
    {
        SnsMessage = snsMessage;
    }

    public Message SnsMessage { get; }
}
