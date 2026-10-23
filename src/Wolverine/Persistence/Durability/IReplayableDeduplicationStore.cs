namespace Wolverine.Persistence.Durability;

/// <summary>
/// GH-4742. Storage for <c>[DeduplicatedWithResponse]</c> HTTP endpoints: a claim that carries the request's
/// fingerprint and, once answered, the response a repeat is replayed with.
///
/// <para>
/// A separate opt-in contract rather than new members on <see cref="IDeduplicationStore" />, and a separate
/// table rather than new columns on <c>wolverine_deduplication</c>, so that plain logical deduplication and
/// its schema are untouched. A message store implements this alongside <see cref="IMessageStore" />; one
/// that does not cannot back the feature.
/// </para>
///
/// <para>
/// Opt-in via <see cref="DurabilitySettings.EnableDeduplicatedResponses" />. When it is off,
/// <see cref="Enabled" /> is <see langword="false" /> and the backing table is not provisioned.
/// </para>
/// </summary>
public interface IReplayableDeduplicationStore
{
    /// <summary>
    /// Is the backing table provisioned? <see langword="false" /> while
    /// <see cref="DurabilitySettings.EnableDeduplicatedResponses" /> is off.
    /// </summary>
    bool Enabled { get; }

    /// <summary>
    /// Claim <paramref name="deduplicationId" /> with the request's <paramref name="fingerprint" />, and a
    /// <paramref name="claimToken" /> unique to this attempt that recording and releasing must present.
    /// <see langword="false" /> when it is already claimed. As <see cref="IDeduplicationStore.TryClaimAsync" />,
    /// this MUST be an INSERT arbitrated by the primary key, never a SELECT followed by an INSERT. Callers pass a UTC
    /// <paramref name="expires" />.
    /// </summary>
    Task<bool> TryClaimAsync(string deduplicationId, string fingerprint, string claimToken, DateTimeOffset expires,
        CancellationToken cancellation = default);

    /// <summary>The claim on <paramref name="deduplicationId" />, or null when there is none.</summary>
    Task<DeduplicatedResponseClaim?> FindAsync(string deduplicationId, CancellationToken cancellation = default);

    /// <summary>
    /// Record the response a repeat of the claimed request is answered with. <see langword="false" /> when the claim
    /// is gone, already answered, or no longer this attempt's, so nothing was recorded.
    /// </summary>
    Task<bool> RecordResponseAsync(string deduplicationId, string claimToken, DeduplicatedResponse response,
        CancellationToken cancellation = default);

    /// <summary>
    /// Release this attempt's claim, but only while it has no response: a failure after the response was recorded
    /// must not let the work run again. Idempotent.
    /// </summary>
    Task ReleaseUnansweredAsync(string deduplicationId, string claimToken, CancellationToken cancellation = default);

    /// <summary>Delete every expired claim, answered or not, and return how many were removed.</summary>
    Task<int> DeleteExpiredAsync(DateTimeOffset utcNow, CancellationToken cancellation = default);
}

/// <summary>GH-4742. A claim: the fingerprint of the request that made it, and its response once answered.</summary>
public sealed record DeduplicatedResponseClaim(string Fingerprint, DeduplicatedResponse? Response);

/// <summary>GH-4742. The response a repeat is replayed with.</summary>
public sealed record DeduplicatedResponse(int StatusCode, string? Body, string? Location);

/// <summary>
/// GH-5160. Does this attempt's claim exist? For a claim written inside the endpoint's transaction: a commit call that fails
/// after the database commit (on Marten, the outbox flush runs inside it) leaves the attempt's own claim committed,
/// which must not be mistaken for a concurrent request's. Internal, and a separate interface, so
/// <see cref="IReplayableDeduplicationStore" /> does not change.
/// </summary>
internal interface IReplayableDeduplicationClaimOwnership
{
    Task<bool> HoldsClaimAsync(string deduplicationId, string claimToken, CancellationToken cancellation = default);
}

/// <summary>
/// GH-5160. A claim to write inside the endpoint's own transaction, with its response, as one row. A null
/// <paramref name="Response" /> is work that was done but whose response could not be stored: the claim is kept
/// unanswered, so repeats are refused rather than run again. Written by the store integration that owns the
/// endpoint's transaction, such as Marten's.
/// </summary>
/// <param name="DeduplicationId">The id the store keys the claim by, already hashed.</param>
public sealed record PendingDeduplicatedResponseClaim(string DeduplicationId, string Fingerprint,
    string ClaimToken, DateTimeOffset Expires, DeduplicatedResponse? Response);
