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
/// Optimistic concurrency exception from Wolverine saga operations. Inherits
/// <see cref="JasperFx.ConcurrencyException"/> (GH-3444) so that a single
/// <c>OnException&lt;ConcurrencyException&gt;()</c> policy catches saga concurrency failures across every
/// storage provider — Marten already surfaces JasperFx's type, and the EF Core / lightweight / CosmosDb
/// saga paths throw this one.
/// </summary>
public class SagaConcurrencyException : JasperFx.ConcurrencyException
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

        // Hand the next contiguous pending message back to the queue -- deliberately ONE, and
        // deliberately WITHOUT advancing LastSequence. The counter has to mean "what has been handled",
        // not "what has been published": the republish is a cascading message that does not leave this
        // context until the current envelope completes, so anything already sitting in the queue is
        // processed BEFORE it. Advancing the counter here let that backlog walk straight through this
        // guard while the replayed message was still in flight. The replayed message's own ShouldProceed
        // advances the counter and hands back the one after it, so the chain continues itself.
        var next = Pending.FirstOrDefault(x => x.Order.HasValue && x.Order.Value == LastSequence + 1);
        if (next != null)
        {
            Pending.Remove(next);
            await bus.PublishAsync(next);
        }

        return true;
    }
}