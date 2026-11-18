using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;

// GH-5660 SPIKE. See IWorkflowContext.cs.

// ReSharper disable once CheckNamespace
namespace Wolverine.Workflows;

/// <summary>
/// The base class of every saga the durable workflow generator writes. It holds what every workflow needs: the step it
/// is waiting at, what it is waiting for, and its undo messages. The generated subclass adds the triggering message and
/// the locals that live across a step.
/// </summary>
public abstract class WorkflowSaga : Saga, ICompensatingSaga
{
    public Guid Id { get; set; }

    /// <summary>The step the workflow is waiting at. 0 while the first step runs, -1 once it has finished.</summary>
    public int Step { get; set; }

    /// <summary>What the current step is waiting for: a message type name, or "delay"</summary>
    public string? WaitingFor { get; set; }

    /// <summary>The undo messages registered with OnFailure, oldest first</summary>
    public List<WorkflowUndo> Undo { get; set; } = new();

    public SagaStatus Status { get; set; } = SagaStatus.Active;

    public List<CompensationLogEntry> CompensationLog { get; set; } = new();

    /// <summary>End the current step: the workflow now waits at <paramref name="step" /></summary>
    protected void SuspendAt(int step, string waitingFor)
    {
        Step = step;
        WaitingFor = waitingFor;
    }

    /// <summary>The workflow method returned</summary>
    protected void CompleteWorkflow()
    {
        Step = -1;
        WaitingFor = null;
        MarkCompleted();
    }

    /// <summary>
    /// The workflow failed for good. Sends the undo messages, newest first, in this handler's transaction, so they
    /// and the saga's new status commit together.
    /// </summary>
    protected async Task CompensateAsync(WorkflowFailed failed, IMessageContext messageContext)
    {
        if (Status != SagaStatus.Active) return;

        // The failed attempt rolled back, so an undo message it registered itself is only on the failure message
        var undo = failed.Undo ?? Undo;

        Status = SagaStatus.Compensating;
        await sendNewestFirstAsync(undo, messageContext);

        Status = SagaStatus.Compensated;
        CompensationLog.Add(new CompensationLogEntry(failed.FailedMessageType, failed.ExceptionType,
            CompensationOutcome.Compensated, DateTimeOffset.UtcNow));

        Step = -1;
        WaitingFor = null;
        MarkCompleted();
    }

    /// <summary>The workflow failed in its first step, so there is no saga to update: just send the undo messages</summary>
    protected static Task CompensateWithoutStateAsync(WorkflowFailed failed, IMessageContext messageContext)
    {
        return sendNewestFirstAsync(failed.Undo ?? [], messageContext);
    }

    protected static void LogNotFound(IMessageContext messageContext, string workflowName, object message)
    {
        if (messageContext is MessageBus bus)
        {
            bus.Runtime.Logger.LogInformation(
                "Durable workflow {Workflow} ({SagaId}) has finished or never started, so {MessageType} was ignored",
                workflowName, messageContext.Envelope?.SagaId, message.GetType().Name);
        }
    }

    private static async Task sendNewestFirstAsync(List<WorkflowUndo> undo, IMessageContext messageContext)
    {
        for (var i = undo.Count - 1; i >= 0; i--)
        {
            await messageContext.SendAsync(undo[i].ToMessage());
        }
    }
}

/// <summary>
/// One undo message, stored on the saga as JSON with its type name. A spike shortcut: the real build would use
/// Wolverine's message type names and the store's serializer.
/// </summary>
public class WorkflowUndo
{
    public string MessageType { get; set; } = "";
    public string Json { get; set; } = "";

    public static WorkflowUndo For(object message)
    {
        return new WorkflowUndo
        {
            MessageType = message.GetType().AssemblyQualifiedName!,
            Json = JsonSerializer.Serialize(message, message.GetType())
        };
    }

    public object ToMessage()
    {
        var type = Type.GetType(MessageType, throwOnError: true)!;
        return JsonSerializer.Deserialize(Json, type)!;
    }
}

/// <summary>
/// Sent to the workflow's saga when it has failed for good. The generated saga declares a subclass of its own, so no
/// two saga types handle the same message type.
/// </summary>
public abstract class WorkflowFailed
{
    public Guid Id { get; set; }

    /// <summary>The undo messages as of the failed attempt, including any it registered before it threw</summary>
    public List<WorkflowUndo>? Undo { get; set; }

    public string FailedMessageType { get; set; } = "";
    public string ExceptionType { get; set; } = "";
    public string ExceptionMessage { get; set; } = "";
}

public static class WorkflowFailure
{
    private const string IdKey = "wolverine.workflow.id";
    private const string UndoKey = "wolverine.workflow.undo";

    /// <summary>Carry the failed attempt's workflow id and undo messages on the exception, past its rollback</summary>
    public static void Record(Exception exception, WorkflowSaga saga)
    {
        exception.Data[IdKey] = saga.Id;
        exception.Data[UndoKey] = saga.Undo.ToList();
    }

    /// <summary>
    /// The error policy's last action once a workflow step has used up its retries: tell the workflow's saga, which
    /// sends the undo messages in its own transaction. A spike shortcut: the real build hangs this on #5556's
    /// "retries used up" hook, so the user's own error policies apply.
    /// </summary>
    public static async ValueTask CompensateAsync<TFailed>(IWolverineRuntime runtime, IEnvelopeLifecycle lifecycle,
        Exception exception) where TFailed : WorkflowFailed, new()
    {
        if (exception.Data[IdKey] is not Guid id)
        {
            runtime.Logger.LogError(exception,
                "A durable workflow step failed before it reached the workflow, so it can't be compensated");
            return;
        }

        var failed = new TFailed
        {
            Id = id,
            Undo = exception.Data[UndoKey] as List<WorkflowUndo>,
            FailedMessageType = lifecycle.Envelope?.MessageType ?? "",
            ExceptionType = exception.GetType().FullName ?? exception.GetType().Name,
            ExceptionMessage = exception.Message
        };

        var bus = new MessageContext(runtime);
        await bus.SendAsync(failed);
        await bus.FlushOutgoingMessagesAsync();
    }
}
