using Wolverine.Persistence.Sagas;

namespace MartenTests.Workflows.Engine;

// GH-5654 SPIKE. The workflow stream holds the workflow's real messages as events (the trigger, every message it
// sent, every message it received), told apart by the "wf" event header. These framework events cover only what
// is not a message.

public static class WorkflowHeaders
{
    /// <summary>"trigger", "sent", "received" or "undo" on a message stored as an event</summary>
    public const string Role = "wf";

    /// <summary>The positional step a sent message was made at</summary>
    public const string Step = "wf-step";

    /// <summary>On an InvokeAsync request: the reply type it waits for</summary>
    public const string Awaits = "wf-awaits";

    public const string Trigger = "trigger";
    public const string Sent = "sent";
    public const string Received = "received";
    public const string Undo = "undo";
}

/// <summary>The first event of every workflow stream</summary>
public record WorkflowStarted(string WorkflowName, int Version, string Fingerprint);

/// <summary>A value recorded once by Now(), NewGuid() or RunAsync()</summary>
public record WorkflowValueRecorded(int Step, string Kind, string Json);

/// <summary>An OnFailure() registration</summary>
public record WorkflowFailureRegistered(int Step, string MessageType, string Json);

/// <summary>The WaitForAsync at this step timed out</summary>
public record WorkflowTimedOut(int Step);

/// <summary>The DelayAsync at this step has elapsed</summary>
public record WorkflowDelayElapsed(int Step);

public record WorkflowCompleted;

public record WorkflowFaulted(string Reason);

// Same names as the #5415 Q6 framework events, so CritterWatch would show the same status as for sagas
public record SagaCompensating(string FailedMessageType, string ExceptionType, string ExceptionMessage);

public record SagaCompensated(int UndoMessagesSent);

// Framework MESSAGES (scheduled, stored as sent messages like any other send). When one arrives it is recorded as
// WorkflowTimedOut / WorkflowDelayElapsed instead of being stored as itself.

public record WorkflowTimeout([property: SagaIdentity] Guid WorkflowId, int Step, string WaitingFor);

public record WorkflowDelay([property: SagaIdentity] Guid WorkflowId, int Step);
