using Amazon.SQS.Model;

namespace Wolverine.AmazonSqs.Internal;

internal class AmazonSqsEnvelope : Envelope
{
    public AmazonSqsEnvelope(Message message)
    {
        SqsMessage = message;
    }

    public Message SqsMessage { get; }
    public bool WasDeleted { get; set; }
}