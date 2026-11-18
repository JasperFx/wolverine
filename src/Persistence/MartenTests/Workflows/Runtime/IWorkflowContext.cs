// GH-5660 SPIKE. The runtime half of the durable workflow source generator. It lives in the test project so no shipped
// package changes; the namespace is the one it would have in Wolverine, because the generator looks the types up by name.

// ReSharper disable once CheckNamespace
namespace Wolverine.Workflows;

/// <summary>
/// Marks a static method as a durable workflow. The method takes the triggering message first, then
/// <see cref="IWorkflowContext" />, then any services. Wolverine.SourceGeneration compiles it into a saga: every await
/// on a member of the context that waits for a message (<see cref="IWorkflowContext.InvokeAsync{TReply}" />,
/// <see cref="IWorkflowContext.WaitForAsync{T}" />, <see cref="IWorkflowContext.DelayAsync" />) ends one saga step.
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

    /// <summary>Sent through the outbox in the step's transaction. Doesn't end the step.</summary>
    ValueTask SendAsync(object message, DeliveryOptions? options = null);

    /// <summary>Published through the outbox in the step's transaction. Doesn't end the step.</summary>
    ValueTask PublishAsync(object message, DeliveryOptions? options = null);

    /// <summary>
    /// Send <paramref name="request" /> and wait for a <typeparamref name="TReply" />, which comes back to the workflow
    /// through the saga-id header. Ends the step.
    /// </summary>
    Task<TReply> InvokeAsync<TReply>(object request, DeliveryOptions? options = null) where TReply : class;

    /// <summary>Wait for a <typeparamref name="T" />. Returns null on a timeout, never throws for one. Ends the step.</summary>
    Task<T?> WaitForAsync<T>(TimeSpan timeout) where T : class;

    /// <summary>A scheduled message to the workflow itself. Ends the step.</summary>
    Task DelayAsync(TimeSpan delay);

    DateTimeOffset Now();

    Guid NewGuid();

    /// <summary>How a workflow does I/O. Runs in place; doesn't end the step.</summary>
    Task<T> RunAsync<T>(Func<Task<T>> action);

    /// <summary>How a workflow does I/O. Runs in place; doesn't end the step.</summary>
    Task RunAsync(Func<Task> action);

    /// <summary>
    /// Register an undo message before the step it undoes. If the workflow fails for good, every registered undo
    /// message is sent, newest first, in one outbox transaction.
    /// </summary>
    void OnFailure(object undoMessage);
}
