namespace Wolverine.Persistence;

public enum InboxUsage
{
    /// <summary>
    /// No interaction with the transactional inbox
    /// </summary>
    None,
    
    /// <summary>
    /// The transactional middleware will mark the current message as handled as part of the current transaction
    /// This assumes the message was received at an endpoint where the message was first persisted into the inbox
    /// storage
    /// </summary>
    MarkAsHandled,
    
    /// <summary>
    /// The transactional middleware will attempt to carry out an idempotency check on the incoming message
    /// as part of the transaction and reject the message if the message & destination has already been handled
    /// </summary>
    IdempotencyCheck
}