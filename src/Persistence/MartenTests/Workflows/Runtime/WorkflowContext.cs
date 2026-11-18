using Microsoft.Extensions.Logging;
using Wolverine.Runtime;

// GH-5660 SPIKE. See IWorkflowContext.cs.

// ReSharper disable once CheckNamespace
namespace Wolverine.Workflows;

/// <summary>
/// The <see cref="IWorkflowContext" /> a generated saga step runs with. The workflow's own code between two waits is
/// copied into the step as written, so its calls to the context land here. The waiting members never do: the generator
/// rewrites each of those into the end of a step.
/// </summary>
public sealed class WorkflowContext : IWorkflowContext
{
    private readonly WorkflowSaga _saga;
    private readonly IMessageContext _messageContext;

    public WorkflowContext(WorkflowSaga saga, IMessageContext messageContext)
    {
        _saga = saga;
        _messageContext = messageContext;
    }

    public Guid WorkflowId => _saga.Id;

    public ValueTask SendAsync(object message, DeliveryOptions? options = null)
    {
        return _messageContext.SendAsync(message, correlate(options));
    }

    public ValueTask PublishAsync(object message, DeliveryOptions? options = null)
    {
        return _messageContext.PublishAsync(message, correlate(options));
    }

    public Task<TReply> InvokeAsync<TReply>(object request, DeliveryOptions? options = null) where TReply : class
    {
        throw notGenerated(nameof(InvokeAsync));
    }

    public Task<T?> WaitForAsync<T>(TimeSpan timeout) where T : class
    {
        throw notGenerated(nameof(WaitForAsync));
    }

    public Task DelayAsync(TimeSpan delay)
    {
        throw notGenerated(nameof(DelayAsync));
    }

    // Code between two waits runs once per attempt, not on every replay, so these need no recording
    public DateTimeOffset Now() => DateTimeOffset.UtcNow;

    public Guid NewGuid() => Guid.NewGuid();

    public Task<T> RunAsync<T>(Func<Task<T>> action) => action();

    public Task RunAsync(Func<Task> action) => action();

    public void OnFailure(object undoMessage)
    {
        _saga.Undo.Add(WorkflowUndo.For(undoMessage));
    }

    /// <summary>Schedule the message that ends a WaitForAsync with null, or a DelayAsync</summary>
    public ValueTask ScheduleTimeoutAsync(TimeoutMessage timeout)
    {
        return _messageContext.ScheduleAsync(timeout, timeout.DelayTime, correlate(null));
    }

    /// <summary>A message the workflow isn't waiting for at its current step: a duplicate, or one that came late</summary>
    public void Ignore(object message)
    {
        if (_messageContext is MessageBus bus)
        {
            bus.Runtime.Logger.LogInformation(
                "Durable workflow saga {SagaType} ({SagaId}) is at step {Step} waiting for {WaitingFor}, so {MessageType} was ignored",
                _saga.GetType().Name, _saga.Id, _saga.Step, _saga.WaitingFor, message.GetType().Name);
        }
    }

    public void RecordFailure(Exception exception) => WorkflowFailure.Record(exception, _saga);

    /// <summary>For a triggering message with no member that names the workflow: the saga-id header, or a new id</summary>
    public static Guid IdFromEnvelope(IMessageContext messageContext)
    {
        return Guid.TryParse(messageContext.Envelope?.SagaId, out var id) ? id : Guid.NewGuid();
    }

    // Every message a workflow sends carries its id, so a reply finds its way back through the saga-id header
    private DeliveryOptions correlate(DeliveryOptions? options)
    {
        options ??= new DeliveryOptions();
        options.SagaId ??= _saga.Id.ToString();
        return options;
    }

    private static NotSupportedException notGenerated(string member)
    {
        return new NotSupportedException(
            $"IWorkflowContext.{member}() ends a workflow step; it only works inside a [DurableWorkflow] method that Wolverine.SourceGeneration compiled into a saga");
    }
}
