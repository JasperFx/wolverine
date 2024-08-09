namespace Wolverine;

#region sample_StatefulSagaOf

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
    /// be incremented upon save
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
}