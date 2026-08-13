using Amazon.SQS.Model;

namespace Wolverine.AmazonSqs.Internal;

internal class AmazonSqsEnvelope : Envelope
{
    public AmazonSqsEnvelope(Message message) : this([message])
    {
    }

    /// <summary>
    ///     GH-3926: a fragmented message arrives as several SQS messages, and completing the envelope has
    ///     to delete all of them. Every other envelope holds exactly one.
    /// </summary>
    public AmazonSqsEnvelope(Message[] messages)
    {
        SqsMessages = messages;
    }

    public Message SqsMessage => SqsMessages[0];

    public Message[] SqsMessages { get; }

    public bool WasDeleted { get; set; }
}
