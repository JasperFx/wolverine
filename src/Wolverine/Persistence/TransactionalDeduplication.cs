using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.Persistence;

/// <summary>
/// GH-4505. The frames a persistence provider contributes when it can write a logical deduplication
/// claim inside its OWN transaction, instead of on the separate connection
/// <see cref="Durability.IDeduplicationStore" /> opens.
///
/// <para>
/// A provider that supplies these replaces the claim-and-release pair outright — the claim rolls back
/// with the handler's work, so there is no compensating release to emit, to order against the response,
/// or to get wrong. What the provider owes in exchange is an answer for the one duplicate an
/// uncommitted claim cannot detect: two concurrent callers who both read "not claimed". See
/// <see cref="CommitRaceWrapper" />.
/// </para>
/// </summary>
public sealed class TransactionalDeduplication
{
    /// <summary>
    /// Asks whether the id is already claimed, producing <see cref="IsDuplicate" />. Optimistic by
    /// nature: a claim that has not been committed yet is invisible to everyone else, so this can only
    /// refuse a caller who is genuinely late, not one who is genuinely concurrent.
    /// </summary>
    public required Frame Check { get; init; }

    /// <summary>
    /// <see langword="true" /> when <see cref="Check" /> found an existing claim. Fed to the chain's own
    /// <see cref="Configuration.IChain.BuildDeduplicationStopCondition" />, so a refusal looks identical
    /// on this path and the non-transactional one.
    /// </summary>
    public required Variable IsDuplicate { get; init; }

    /// <summary>
    /// Enlists the claim in the provider's unit of work. Must not write anything on its own — the whole
    /// guarantee is that the claim lands if and only if the surrounding transaction commits.
    /// </summary>
    public required Frame Claim { get; init; }

    /// <summary>
    /// A frame that wraps the remainder of the chain — handler, commit, and response writing — and
    /// converts a commit that lost the race for this id into the same refusal <see cref="Check" />
    /// produces. Optional only in the sense that a provider whose claim cannot collide has nothing to
    /// wrap; every relational provider needs one.
    /// </summary>
    public Frame? CommitRaceWrapper { get; init; }
}
