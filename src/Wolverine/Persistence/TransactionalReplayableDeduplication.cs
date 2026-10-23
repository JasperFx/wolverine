using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Wolverine.Configuration;

namespace Wolverine.Persistence;

/// <summary>
/// GH-5160. A persistence provider that can write a <c>[DeduplicatedWithResponse]</c> claim, with the response a repeat
/// is answered with, inside its OWN transaction. The <c>[DeduplicatedWithResponse]</c> twin of
/// <see cref="IPersistenceFrameProvider.TryBuildTransactionalDeduplication" />.
///
/// <para>
/// A sibling of <see cref="IPersistenceFrameProvider" /> rather than a new member on it, as
/// <see cref="EventSourcing.IEventSourcingFrameProvider" /> is: implemented on the same class, and found by a
/// type test on the provider that owns the chain's transaction. A provider that does not implement it keeps the
/// claim-and-release path. Internal for now: only Wolverine.Marten implements it, and only Wolverine.Http calls it,
/// so it can change shape as other stores follow without being a published extension point.
/// </para>
///
/// <para>
/// Claim-and-release commits the claim before the endpoint runs and records the response after the endpoint
/// commits, each on a connection of its own. A crash between the claim and the commit leaves a claim with no
/// work and no response, and a failed record leaves committed work with no response; either way every repeat
/// is refused with 409 until the claim expires. Written in the endpoint's transaction, the claim and its
/// response commit with the work or not at all.
/// </para>
/// </summary>
internal interface ITransactionalReplayableDeduplicationProvider
{
    /// <summary>
    /// Supply the frames that enlist <paramref name="claim" /> in this provider's unit of work, or
    /// <see langword="false" /> to keep claim-and-release.
    /// </summary>
    /// <remarks>
    /// Return <see langword="true" /> only when the chain really commits through this provider, and the message store
    /// the claim is read back from holds the <c>wolverine_deduplicated_responses</c> table in the database that commit
    /// writes to. That is stricter than <see cref="IPersistenceFrameProvider.TryBuildTransactionalDeduplication" />,
    /// which does not check where the message store lives.
    /// </remarks>
    /// <param name="chain">The chain being woven.</param>
    /// <param name="deduplicationId">
    /// The id the store keys the claim by, already hashed; null at runtime when the request has no key.
    /// </param>
    /// <param name="canBatch">
    /// Whether the check may join a batched query the chain was already running. False when the key is optional, as
    /// for <see cref="IPersistenceFrameProvider.TryBuildTransactionalDeduplication" />: a request without one must cost
    /// no round trip.
    /// </param>
    /// <param name="claim">
    /// A <see cref="Durability.PendingDeduplicatedResponseClaim" />, null at runtime when there is nothing to
    /// claim: no key, or the endpoint failed.
    /// </param>
    /// <param name="container">Active codegen service container.</param>
    /// <param name="deduplication">The frames, when this provider owns the claim.</param>
    bool TryBuildTransactionalReplayableDeduplication(IChain chain, Variable deduplicationId, bool canBatch,
        Variable claim, IServiceContainer container,
        [NotNullWhen(true)] out TransactionalReplayableDeduplication? deduplication);
}

/// <summary>
/// GH-5160. The frames a provider contributes to write a <c>[DeduplicatedWithResponse]</c> claim inside its own
/// transaction. See <see cref="ITransactionalReplayableDeduplicationProvider" />.
/// </summary>
internal sealed class TransactionalReplayableDeduplication
{
    /// <summary>
    /// Reads an earlier request's committed claim, through the provider's own session, producing
    /// <see cref="ExistingClaim" />. As <see cref="TransactionalDeduplication.Check" />: optimistic, since a claim not
    /// yet committed is invisible, and free to join a batched query the chain was already running.
    /// </summary>
    public required Frame Check { get; init; }

    /// <summary>
    /// A <see cref="Durability.DeduplicatedResponseClaim" />, or null when there is none: what a repeat is answered
    /// from before any work happens.
    /// </summary>
    public required Variable ExistingClaim { get; init; }

    /// <summary>
    /// Enlists the claim in the provider's unit of work. Must not write anything on its own: the claim lands if
    /// and only if <see cref="Commit" /> does.
    /// </summary>
    public required Frame Claim { get; init; }

    /// <summary>
    /// The chain's own commit frame, already in its postprocessors. <see cref="Claim" /> is placed just before it,
    /// after the endpoint has set the status and <c>Location</c> the claim records.
    /// </summary>
    /// <remarks>
    /// No classifier for a commit that lost the race, unlike <see cref="TransactionalDeduplication" />: a concurrent
    /// repeat's commit can fail on the endpoint's own work (the same stream, a versioned document) before it reaches
    /// the claim. A failed commit is answered from a claim committed in the meantime, whatever the failure was.
    /// </remarks>
    public required Frame Commit { get; init; }
}
