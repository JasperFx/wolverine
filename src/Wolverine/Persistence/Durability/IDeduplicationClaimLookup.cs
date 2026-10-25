namespace Wolverine.Persistence.Durability;

/// <summary>
/// GH-5206. Optionally implemented by an <see cref="IDeduplicationStore" /> that can say whether a logical
/// deduplication id is claimed without claiming it. An HTTP <c>[Deduplicated]</c> endpoint whose claim rides its own
/// transaction uses it to answer a concurrent repeat that failed over the first request's committed work.
/// </summary>
public interface IDeduplicationClaimLookup
{
    /// <summary>
    /// Is <paramref name="deduplicationId" /> claimed? An expired claim the cleanup has not removed yet counts, as it
    /// does for <see cref="IDeduplicationStore.TryClaimAsync" />.
    /// </summary>
    Task<bool> IsClaimedAsync(string deduplicationId, CancellationToken cancellation = default);
}
