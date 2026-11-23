namespace Wolverine.Persistence;

/// <summary>
/// GH-5613. How a <c>[Deduplicated]</c> execution with an id ended, as recorded in telemetry by
/// <see cref="Configuration.IChain.BuildDeduplicationOutcomeFrame" />.
/// </summary>
public enum DeduplicatedExecutionOutcome
{
    /// <summary>
    /// The execution did its work, and its claim on the id is kept.
    /// </summary>
    Claimed,

    /// <summary>
    /// The execution was refused, because its id is already claimed.
    /// </summary>
    Duplicate
}
