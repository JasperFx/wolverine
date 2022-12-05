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
}

#endregion