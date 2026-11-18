// GH-5556 SPEC STUBS: the names Jeremy approved as working names (2026-11-15). Specified by Bobcat specifications
// first; nothing in Wolverine reads or writes these yet.

// ReSharper disable once CheckNamespace
namespace Wolverine;

/// <summary>
/// GH-5556. Opt in to visible compensation state on a saga: its <see cref="Status" /> and a short
/// <see cref="CompensationLog" />, both stored in the saga body, so no schema change for document or lightweight
/// JSON storage. A saga that does not implement it stores exactly the JSON it stores today.
/// </summary>
public interface ICompensatingSaga
{
    /// <summary>Where the saga is in its compensation, if anywhere.</summary>
    SagaStatus Status { get; set; }

    /// <summary>One entry per compensation Wolverine ran, oldest first.</summary>
    List<CompensationLogEntry> CompensationLog { get; set; }
}

/// <summary>GH-5556. Where a <see cref="ICompensatingSaga" /> is in its compensation.</summary>
public enum SagaStatus
{
    Active,
    Compensating,
    Compensated,
    CompensationFailed
}

/// <summary>GH-5556. How one compensation ended.</summary>
public enum CompensationOutcome
{
    Compensated,
    Failed
}

/// <summary>
/// GH-5556. One compensation: the failed step's message type, the exception type it failed with, the outcome and
/// when it happened.
/// </summary>
public record CompensationLogEntry(string Step, string ExceptionType, CompensationOutcome Outcome, DateTimeOffset At);
