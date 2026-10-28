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

    /// <summary>
    ///     The <c>MessageId</c> of the SNS notification this message was delivered in, when an SNS subscription
    ///     without raw message delivery wrapped it. Set by the mappers that unwrap SNS notifications, and read by
    ///     <see cref="SqsMessageIdentity" />: SNS can deliver one notification to a queue twice, under two
    ///     different SQS message ids, and only this id is the same on both.
    /// </summary>
    public string? SnsMessageId { get; set; }
}
