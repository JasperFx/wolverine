namespace Wolverine.RDBMS.Transport;

/// <summary>
/// GH-5775. A database queue whose receive deletes the row could not put a deferred message back into its
/// table. The message is no longer in the queue, so this is a lost message unless something else holds it.
/// </summary>
public class DatabaseQueueReinsertFailedException : Exception
{
    public DatabaseQueueReinsertFailedException(Guid envelopeId, string queueName, int attempts, Exception inner)
        : base(
            $"Message {envelopeId} was removed from database queue {queueName} when it was received and could not be put back after {attempts} attempt(s). The message is no longer in the queue.",
            inner)
    {
        EnvelopeId = envelopeId;
        QueueName = queueName;
        Attempts = attempts;
    }

    public Guid EnvelopeId { get; }
    public string QueueName { get; }
    public int Attempts { get; }
}
