namespace Wolverine.Persistence;

/// <summary>
/// GH-4180. Which of the two ways a logical deduplication check can refuse to run the chain.
///
/// <para>
/// These are kept apart rather than collapsed into one "rejected" state because they mean opposite
/// things operationally, and the response each deserves is different in every chain type. A
/// <see cref="Duplicate" /> is the feature working: the traffic is fine, the work has already been
/// done, and the right answer is a benign refusal. A <see cref="MissingId" /> is a bug in the
/// caller or the configuration: nothing has been done, nothing will be, and treating it as benign
/// would silently drop real work.
/// </para>
/// </summary>
public enum DeduplicationOutcome
{
    /// <summary>
    /// The logical id was resolved and has already been claimed inside the configured
    /// <see cref="DurabilitySettings.DeduplicationWindow" />.
    /// </summary>
    Duplicate,

    /// <summary>
    /// The chain requires a logical id (<see cref="DeduplicationRequirement.Required" />) and the
    /// incoming message, request, or call did not carry one.
    /// </summary>
    MissingId
}
