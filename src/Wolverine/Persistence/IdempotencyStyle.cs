namespace Wolverine.Persistence;

public enum IdempotencyStyle
{
    /// <summary>
    /// No additional idempotency checks will be made by Wolverine for this
    /// message handler. Only applies to messages received at "Inline" or "Buffered" endpoints
    /// </summary>
    None,
    
    /// <summary>
    /// Message idempotency would be checked at the time the current transaction
    /// is being committed. This mode is a little more optimal as it allows for more database command batching,
    /// but should not be used if the message handling involves any actions not part of the current transaction
    /// like calls to external web services
    ///
    /// NOT CURRENTLY REACHABLE. As of 5.4.1 every persistence provider -- EF Core, Marten, Polecat and Fisher --
    /// emits the <see cref="Eager" /> check for this style as well, so choosing Optimistic behaves exactly like
    /// choosing Eager at runtime. Reserved rather than removed so that existing configuration keeps compiling.
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