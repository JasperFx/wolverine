namespace Wolverine;

#region sample_statefulsagaof
/// <summary>
///     Base class for implementing handlers for a stateful saga
/// </summary>
public abstract class Saga
{
    private bool _isCompleted;

    /// <summary>
    ///     Is the current stateful saga complete? If so,
    ///     Wolverine will delete this document at the end
    ///     of the current message handling
    /// </summary>
    public bool IsCompleted()
    {
        return _isCompleted;
    }

    /// <summary>
    ///     Called to mark this saga as "complete"
    /// </summary>
    protected void MarkCompleted()
    {
        _isCompleted = true;
    }
    
    /// <summary>
    /// For saga providers that support this, this is a version of the saga to help enforce optimistic concurrency
    /// protections. This value is the current version that is stored by saga storage and will
    /// be incremented upon save.
    /// Typed as <see cref="int"/> to align with <c>JasperFx.IRevisioned.Version</c>
    /// (an <see cref="int"/>), so sagas can implement <c>IRevisioned</c> directly
    /// without a shadow override. (JasperFx 2.0 rc split versioning into
    /// <c>IRevisioned</c> = <see cref="int"/> and <c>ILongVersioned</c> = <see cref="long"/>;
    /// sagas use the <see cref="int"/> revision.)
    /// </summary>
    public int Version { get; set; }
}

#endregion

/// <summary>
/// Optimistic concurrency exception from Wolverine saga operations
/// </summary>
public class SagaConcurrencyException : Exception
{
    public SagaConcurrencyException(string message) : base(message)
    {
    }

    /// <summary>
    /// Keeps the underlying store's own concurrency failure (say, a CosmosDB 412 Precondition Failed)
    /// attached, so error handling policies and logs can still see what the database actually said
    /// </summary>
    public SagaConcurrencyException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public interface SequencedMessage
{
    int? Order { get; }
}

// Use code gen to "know" how to get the sequence?
public abstract class ResequencerSaga<T> : Saga where T : SequencedMessage
{
    public List<T> Pending { get; set; } = new();
    public int LastSequence { get; set; }

    // We'll enhance the code gen to use this around the Saga handling. So this would wrap
    // around the call to the actual Handle method as a guard clause, but the saga still gets persisted
    public async ValueTask<bool> ShouldProceed(T message, IMessageBus bus)
    {
        // TODO -- probably want a Timeout around this?
        
        // If there is no order, do you just let it go? Or zero?
        if (!message.Order.HasValue || message.Order == 0)
        {
            return true;
        }

        // Already processed in sequence, allow re-published messages through
        if (message.Order.Value <= LastSequence)
        {
            return true;
        }

        if (message.Order.Value != LastSequence + 1)
        {
            Pending.Add(message);
            return false;
        }
        
        // It can go ahead
        LastSequence = message.Order.Value;

        // Try to recover any pending messages
        while (Pending.Any())
        {
            var next = Pending.FirstOrDefault(x => x.Order.HasValue && x.Order.Value == LastSequence + 1);
            if (next == null)
            {
                break;
            }

            Pending.Remove(next);
            LastSequence = next.Order!.Value;

            // This doesn't actually go out until the original message completes
            await bus.PublishAsync(next);
        }

        return true;
    }
}