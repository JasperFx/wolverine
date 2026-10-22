namespace Wolverine.Persistence.Durability;

/// <summary>
/// D1 (durable compliance findings, 2026-10-22). An opt-in companion to <see cref="IDeduplicationStore" /> for a
/// store whose claim is committed on its own connection, ahead of the handler, and given back only by the
/// compensating release. Such a claim outlives a node that dies mid-handler, and the release never runs. When
/// inbox recovery then brings the SAME envelope back, a plain <see cref="IDeduplicationStore.TryClaimAsync" />
/// refuses it as a duplicate of its own stranded claim, and the work is lost for the whole window.
///
/// <para>
/// A store that implements this records which envelope took each claim. A later claim for the same id from the
/// SAME envelope takes the claim over and runs; a different envelope with the same id is still refused. A claim
/// recorded without an envelope (written through <see cref="IDeduplicationStore.TryClaimAsync" />, or before the
/// store recorded envelopes at all) can never be taken over, which is the original behaviour.
/// </para>
///
/// <para>
/// A separate interface rather than a new member on <see cref="IDeduplicationStore" />, because that is a
/// documented extension point and a third-party implementation must keep compiling and loading unchanged.
/// Wolverine's message handlers use this when the store offers it and fall back to
/// <see cref="IDeduplicationStore.TryClaimAsync" /> when it does not. A store whose claim already rides the
/// handler's transaction (Marten, Polecat, Fisher, EF Core) has nothing to strand and has no need of it.
/// </para>
/// </summary>
public interface IEnvelopeAwareDeduplicationStore
{
    /// <summary>
    /// Claim <paramref name="deduplicationId" /> for the envelope <paramref name="envelopeId" />. Returns
    /// <see langword="true" /> when this envelope now holds the claim: either nobody held it, or the envelope that
    /// held it was this one. Returns <see langword="false" /> when a different envelope, or a claim recorded with
    /// no envelope at all, holds it.
    /// </summary>
    /// <remarks>
    /// The same atomicity rules as <see cref="IDeduplicationStore.TryClaimAsync" />: the claim is arbitrated by the
    /// database's unique constraint, never a SELECT followed by an INSERT. Taking a claim over is a conditional
    /// UPDATE keyed on both the id and the envelope id, so it cannot hand a claim to the wrong envelope either.
    ///
    /// <para>
    /// A store that cannot record the envelope right now -- Wolverine's relational stores against a table that
    /// predates the column -- degrades to <see cref="IDeduplicationStore.TryClaimAsync" /> exactly: the claim is
    /// recorded with no envelope, and a repeat from the same envelope is refused like any other.
    /// </para>
    /// </remarks>
    Task<bool> TryClaimAsync(string deduplicationId, Guid envelopeId, DateTimeOffset expires,
        CancellationToken cancellation = default);
}
