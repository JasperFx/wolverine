using Wolverine;

namespace MartenTests.Workflows.Engine;

// GH-5654 SPIKE. Nothing in this folder is public Wolverine API; it lives in the test project on purpose.

/// <summary>
/// Marks a static method as a durable workflow. The method takes the triggering message first, then
/// <see cref="IWorkflowContext" />, then any services. On every resume the method runs again from the top
/// and each completed <see cref="IWorkflowContext" /> call returns its stored result from the journal.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class DurableWorkflowAttribute : Attribute
{
    public DurableWorkflowAttribute()
    {
    }

    public DurableWorkflowAttribute(string name)
    {
        Name = name;
    }

    /// <summary>Defaults to "{Class}.{Method}"</summary>
    public string? Name { get; set; }

    public int Version { get; set; } = 1;
}

public interface IWorkflowContext
{
    Guid WorkflowId { get; }

    /// <summary>Recorded by position. Appended to the journal in the same transaction as the outbox write.</summary>
    ValueTask SendAsync(object message, DeliveryOptions? options = null);

    ValueTask PublishAsync(object message, DeliveryOptions? options = null);

    /// <summary>
    /// Send <paramref name="request" /> and wait for a <typeparamref name="TReply" />, correlated by the
    /// workflow id in the saga-id header.
    /// </summary>
    Task<TReply> InvokeAsync<TReply>(object request, DeliveryOptions? options = null) where TReply : class;

    /// <summary>Wait for a <typeparamref name="T" />. Returns null on a timeout, never throws for one.</summary>
    Task<T?> WaitForAsync<T>(TimeSpan timeout) where T : class;

    Task DelayAsync(TimeSpan delay);

    /// <summary>Recorded once, so a replay sees the same value</summary>
    DateTimeOffset Now();

    /// <summary>Recorded once, so a replay sees the same value</summary>
    Guid NewGuid();

    /// <summary>How a workflow does I/O: the result is recorded once and replayed after that</summary>
    Task<T> RunAsync<T>(Func<Task<T>> action);

    /// <summary>
    /// Register an undo message before the step it undoes. If the workflow fails for good, every registered
    /// undo message is sent in LIFO order in one outbox transaction.
    /// </summary>
    void OnFailure(object undoMessage);
}

/// <summary>A replay asked for something the journal does not have at that point</summary>
public class WorkflowReplayMismatchException : Exception
{
    public WorkflowReplayMismatchException(string message) : base(message)
    {
    }
}
