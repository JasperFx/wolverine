namespace Wolverine.Persistence;

public enum IdempotencyStyle
{
    /// <summary>
    /// No additional idempotency checks will be made by Wolverine for this
    /// message handler. Only applies to messages received at "Inline" or "Buffered" endpoints
    /// </summary>
    None,
    
    /// <summary>
    /// Message idempotency will be checked at the time the current transaction
    /// is being committed. This mode is a little more optimal as it allows for more database command batching,
    /// but should not be used if the message handling involves any actions not part of the current transaction
    /// like calls to external web services
    /// 
    /// Only applies to messages received at "Inline" or "Buffered" endpoints
    /// </summary>
    Optimistic,
    
    /// <summary>
    /// Message idempotency will be checked before any other message handling takes place. This is appropriate
    /// for cases where the message handling carries out some kind of action like a call to an external web service
    /// that is not part of the current transaction. This does potentially cause extra network round trips to the database
    ///
    /// Only applies to messages received at "Inline" or "Buffered" endpoints
    /// </summary>
    Eager
}