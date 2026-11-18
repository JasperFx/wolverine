using System.Text.Json;
using JasperFx.Events;
using Marten.Events.Aggregation;

namespace MartenTests.Workflows.Engine;

public enum WorkflowStatus
{
    Running,
    Waiting,
    Completed,
    Faulted,
    Compensating,
    Compensated
}

/// <summary>
/// GH-5654 SPIKE. The framework-owned inline snapshot of a workflow stream: what the saga explorer would list and
/// search. Projected from the stream (Inline), never written by the runner directly.
/// </summary>
public class WorkflowState
{
    public Guid Id { get; set; }
    public string WorkflowName { get; set; } = "";
    // Not "Version": Marten would treat that as the stream version and overwrite it
    public int WorkflowVersion { get; set; }
    public string Fingerprint { get; set; } = "";
    public WorkflowStatus Status { get; set; }

    /// <summary>The message type (or "delay") the workflow is waiting on, if any</summary>
    public string? WaitingFor { get; set; }

    /// <summary>The positional step of that wait; a timeout for any other step is stale</summary>
    public int? WaitingStep { get; set; }

    public DateTimeOffset Started { get; set; }
    public DateTimeOffset LastModified { get; set; }

    public string? TriggeringMessageType { get; set; }
    public string? TriggeringMessage { get; set; }

    public string? FaultReason { get; set; }

    public bool IsFinished => Status is WorkflowStatus.Completed or WorkflowStatus.Faulted
        or WorkflowStatus.Compensated or WorkflowStatus.Compensating;
}

public class WorkflowStateProjection : SingleStreamProjection<WorkflowState, Guid>
{
    public override WorkflowState? Evolve(WorkflowState? snapshot, Guid id, IEvent e)
    {
        snapshot ??= new WorkflowState { Id = id };
        var timestamp = e.Timestamp == default ? DateTimeOffset.UtcNow : e.Timestamp;
        snapshot.LastModified = timestamp;

        switch (e.Data)
        {
            case WorkflowStarted started:
                snapshot.WorkflowName = started.WorkflowName;
                snapshot.WorkflowVersion = started.Version;
                snapshot.Fingerprint = started.Fingerprint;
                snapshot.Started = timestamp;
                snapshot.Status = WorkflowStatus.Running;
                return snapshot;

            case WorkflowTimedOut or WorkflowDelayElapsed:
                clearWait(snapshot);
                return snapshot;

            case WorkflowCompleted:
                clearWait(snapshot);
                snapshot.Status = WorkflowStatus.Completed;
                return snapshot;

            case WorkflowFaulted faulted:
                snapshot.Status = WorkflowStatus.Faulted;
                snapshot.FaultReason = faulted.Reason;
                return snapshot;

            case SagaCompensating:
                snapshot.Status = WorkflowStatus.Compensating;
                return snapshot;

            case SagaCompensated:
                clearWait(snapshot);
                snapshot.Status = WorkflowStatus.Compensated;
                return snapshot;
        }

        switch (e.WorkflowRole())
        {
            case WorkflowHeaders.Trigger:
                snapshot.TriggeringMessageType = e.Data.GetType().FullName;
                snapshot.TriggeringMessage = JsonSerializer.Serialize(e.Data, e.Data.GetType());
                break;

            case WorkflowHeaders.Sent when e.Data is WorkflowTimeout timeout:
                wait(snapshot, timeout.WaitingFor, timeout.Step);
                break;

            case WorkflowHeaders.Sent when e.Data is WorkflowDelay delay:
                wait(snapshot, "delay", delay.Step);
                break;

            case WorkflowHeaders.Sent when e.HeaderValue(WorkflowHeaders.Awaits) is { } awaits:
                wait(snapshot, awaits, int.Parse(e.HeaderValue(WorkflowHeaders.Step)!));
                break;

            case WorkflowHeaders.Received when e.Data.GetType().Name == snapshot.WaitingFor:
                // The awaited message arrived; whatever the workflow does next sets the next wait
                clearWait(snapshot);
                break;
        }

        return snapshot;
    }

    private static void wait(WorkflowState snapshot, string waitingFor, int step)
    {
        snapshot.Status = WorkflowStatus.Waiting;
        snapshot.WaitingFor = waitingFor;
        snapshot.WaitingStep = step;
    }

    private static void clearWait(WorkflowState snapshot)
    {
        snapshot.Status = WorkflowStatus.Running;
        snapshot.WaitingFor = null;
        snapshot.WaitingStep = null;
    }
}

internal static class WorkflowEventExtensions
{
    public static string? HeaderValue(this IEvent e, string key)
    {
        if (e.Headers == null || !e.Headers.TryGetValue(key, out var value) || value == null) return null;

        // In memory it's a string; read back from the database it's a JsonElement
        return value is JsonElement element ? element.ToString() : value.ToString();
    }

    public static string? WorkflowRole(this IEvent e) => e.HeaderValue(WorkflowHeaders.Role);
}
