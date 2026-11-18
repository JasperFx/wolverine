namespace Wolverine.Persistence.Sagas;

// GH-5415. The framework-owned events Wolverine appends to an event-sourced saga's stream when it compensates a failed
// step, from which EventSourcedSaga rebuilds its Status and CompensationLog. Names and payloads are proposals awaiting
// review (SPEC-REVIEW-5415.md); nothing appends them yet.

/// <summary>
/// GH-5415. Wolverine began compensating <paramref name="Step" /> (Wolverine's message type name of the failed message)
/// for an exception of type <paramref name="ExceptionType" /> (its full name). Appended in the same transaction as the
/// events the <c>Compensate</c> method returned.
/// </summary>
public sealed record SagaCompensating(string Step, string ExceptionType);

/// <summary>GH-5415. The compensation of <paramref name="Step" /> for <paramref name="ExceptionType" /> succeeded.</summary>
public sealed record SagaCompensated(string Step, string ExceptionType);

/// <summary>
/// GH-5415. The compensation of <paramref name="Step" /> for <paramref name="ExceptionType" /> failed after its retries,
/// with <paramref name="Reason" /> (the type and message of what the compensation threw).
/// </summary>
public sealed record SagaCompensationFailed(string Step, string ExceptionType, string Reason);
