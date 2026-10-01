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
    /// Claim <paramref name="deduplicationId" /> with the request's <paramref name="fingerprint" />.
    /// <see langword="false" /> when it is already claimed. As <see cref="IDeduplicationStore.TryClaimAsync" />,
    /// this MUST be an INSERT arbitrated by the primary key, never a SELECT followed by an INSERT.
    /// </summary>
    Task<bool> TryClaimAsync(string deduplicationId, string fingerprint, DateTimeOffset expires,
        CancellationToken cancellation = default);

    /// <summary>The claim on <paramref name="deduplicationId" />, or null when there is none.</summary>
    Task<DeduplicatedResponseClaim?> FindAsync(string deduplicationId, CancellationToken cancellation = default);

    /// <summary>Record the response a repeat of the claimed request is answered with.</summary>
    Task RecordResponseAsync(string deduplicationId, DeduplicatedResponse response,
        CancellationToken cancellation = default);

    /// <summary>
    /// Release the claim, but only while it has no response: a failure after the response was recorded must
    /// not let the work run again. Idempotent.
    /// </summary>
    Task ReleaseUnansweredAsync(string deduplicationId, CancellationToken cancellation = default);

    /// <summary>Delete every expired claim, answered or not, and return how many were removed.</summary>
    Task<int> DeleteExpiredAsync(DateTimeOffset utcNow, CancellationToken cancellation = default);
}

/// <summary>GH-4742. A claim: the fingerprint of the request that made it, and its response once answered.</summary>
public sealed record DeduplicatedResponseClaim(string Fingerprint, DeduplicatedResponse? Response);

/// <summary>GH-4742. The response a repeat is replayed with.</summary>
public sealed record DeduplicatedResponse(int StatusCode, string? Body, string? Location);
