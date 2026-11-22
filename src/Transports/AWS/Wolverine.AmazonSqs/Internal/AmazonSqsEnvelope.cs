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
    ///     GH-5753. Set once a requeue's copy has been accepted by SQS, so a retry of the requeue after a failed
    ///     delete of the original does not send a second copy.
    /// </summary>
    public bool RequeueCopySent { get; set; }

    /// <summary>
    ///     GH-5752. The <c>MessageDeduplicationId</c> of this envelope's requeue copy on a FIFO queue. The copy
    ///     cannot reuse the original's id, which SQS would drop inside its 5 minute deduplication window while
    ///     still answering the send with success. Assigned once, so every retry of the same requeue sends the
    ///     same id and an ambiguous send response cannot produce two copies.
    /// </summary>
    public string? RequeueDeduplicationId { get; set; }

    /// <summary>
    ///     GH-5752. The <c>MessageGroupId</c> this message was received under on a FIFO queue, read from the
    ///     SQS system attribute. A raw JSON envelope has no <see cref="Envelope.GroupId" />, and a FIFO queue
    ///     rejects a requeue copy that carries no group.
    /// </summary>
    public string? ReceivedMessageGroupId =>
        SqsMessage.Attributes != null && SqsMessage.Attributes.TryGetValue("MessageGroupId", out var groupId)
            ? groupId
            : null;

    /// <summary>
    ///     The <c>MessageId</c> of the SNS notification this message was delivered in, when an SNS subscription
    ///     without raw message delivery wrapped it. Set by the mappers that unwrap SNS notifications, and read by
    ///     <see cref="SqsMessageIdentity" />: SNS can deliver one notification to a queue twice, under two
    ///     different SQS message ids, and only this id is the same on both.
    /// </summary>
    public string? SnsMessageId { get; set; }
}
