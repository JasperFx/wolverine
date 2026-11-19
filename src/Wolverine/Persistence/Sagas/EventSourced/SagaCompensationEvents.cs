namespace Wolverine.Persistence.Sagas;

// GH-5415. The framework-owned events Wolverine appends to an event-sourced saga's stream when it compensates a failed
// step, from which EventSourcedSaga rebuilds its Status and CompensationLog. Names and payloads are proposals awaiting
// review (SPEC-REVIEW-5415.md); nothing appends them yet.

/// <summary>
/// GH-5415. Wolverine began compensating <paramref name="Step" /> (Wolverine's message type name of the failed message)
/// for an exception of type <paramref name="ExceptionType" /> (its full name). Appended in the same transaction as the
/// events the <c>Compensate</c> method returned.
/// </summary>
public sealed record SagaCompensating(string Step, string ExceptionType)
{
    /// <summary>GH-5673. The envelope id of the failed message, when known</summary>
    public Guid? FailedMessageId { get; init; }

    /// <summary>
    /// GH-5673. The compensation unwinds the undo stack one confirmed undo message at a time, so the saga stays
    /// <c>Compensating</c> until the stack is empty
    /// </summary>
    public bool Sequential { get; init; }

    /// <summary>GH-5673. A sequential unwind undoes entries whose step was never confirmed too</summary>
    public bool IncludePending { get; init; } = true;

    /// <summary>GH-5673. The saga completes once the sequential unwind's last undo message is confirmed</summary>
    public bool CompleteWhenDone { get; init; }
}

/// <summary>GH-5415. The compensation of <paramref name="Step" /> for <paramref name="ExceptionType" /> succeeded.</summary>
public sealed record SagaCompensated(string Step, string ExceptionType)
{
    /// <summary>GH-5673. The undo messages the compensation sent, with their step tags</summary>
    public List<UndoneStep>? Undone { get; init; }
}

/// <summary>
/// GH-5415. The compensation of <paramref name="Step" /> for <paramref name="ExceptionType" /> failed after its retries,
/// with <paramref name="Reason" /> (the type and message of what the compensation threw).
/// </summary>
public sealed record SagaCompensationFailed(string Step, string ExceptionType, string Reason)
{
    /// <summary>GH-5673. The undo messages the compensation sent before it failed, with their step tags</summary>
    public List<UndoneStep>? Undone { get; init; }
}
