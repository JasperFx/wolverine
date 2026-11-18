using JasperFx;

// ReSharper disable once CheckNamespace
namespace Wolverine;

/// <summary>
/// GH-5556. A <see cref="Saga" /> that shows its compensation: its <see cref="Status" /> and a short
/// <see cref="CompensationLog" />, both kept in the saga body. Any <see cref="Saga" /> may declare <c>Compensate</c>
/// methods; this base type only adds the visible state. A saga that does not derive from it stores exactly what it
/// stores today.
/// </summary>
/// <remarks>
/// It implements <see cref="IRevisioned" />, so Marten, Polecat and Fisher check its version when they save it, as the
/// lightweight saga tables always do: a message that saves the saga while it is being compensated is then a
/// concurrency conflict, retried under the compensation's error policies.
/// </remarks>
public abstract class CompensatingSaga : Saga, IRevisioned
{
    /// <summary>Where the saga is in its compensation, if anywhere. Set by Wolverine.</summary>
    public SagaStatus Status { get; set; } = SagaStatus.Active;

    /// <summary>One entry per compensation Wolverine ran, oldest first. Written by Wolverine.</summary>
    public List<CompensationLogEntry> CompensationLog { get; set; } = [];
}

/// <summary>GH-5556. Where a <see cref="CompensatingSaga" /> is in its compensation.</summary>
public enum SagaStatus
{
    /// <summary>Nothing has been compensated</summary>
    Active,

    /// <summary>Reserved for a compensation that is in progress. Wolverine does not save this status today</summary>
    Compensating,

    /// <summary>The last compensation succeeded</summary>
    Compensated,

    /// <summary>The last compensation failed, after its retries</summary>
    CompensationFailed
}

/// <summary>GH-5556. How one compensation ended.</summary>
public enum CompensationOutcome
{
    Compensated,
    Failed
}

/// <summary>
/// GH-5556. One compensation: the failed step (Wolverine's message type name of the failed message), the full name of
/// the exception type that was actually thrown, the outcome and when it happened.
/// </summary>
public record CompensationLogEntry(string Step, string ExceptionType, CompensationOutcome Outcome, DateTimeOffset At);
