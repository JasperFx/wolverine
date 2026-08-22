namespace Wolverine.Persistence;

/// <summary>
/// Optional capability for an <see cref="IClaimCheckStore"/> that can find and delete its own aged
/// payloads. Implementing this interface opts the backend into Wolverine's claim-check sweeper, which
/// is enabled by <see cref="ClaimCheckConfiguration.DeletePayloadsOlderThan"/>. See GH-3509.
/// </summary>
/// <remarks>
/// This is deliberately a separate interface rather than another method on <see cref="IClaimCheckStore"/>:
/// adding to the core contract would break every third-party backend, and several first-party backends
/// legitimately should <i>not</i> implement it. Azure Blob Storage, Amazon S3, and Google Cloud Storage all
/// support server-side lifecycle rules that expire objects for free, whereas a Wolverine-driven sweep over
/// those stores would mean paying for LIST operations across the whole bucket on every pass. Those backends
/// deliberately do not implement this interface; point their native lifecycle policies at the claim-check
/// prefix instead.
/// </remarks>
public interface IClaimCheckStoreWithExpiration : IClaimCheckStore
{
    /// <summary>
    /// Delete at most <paramref name="maxCount"/> payloads that were stored before <paramref name="cutoff"/>,
    /// and return how many were actually deleted. Returning <paramref name="maxCount"/> tells the sweeper
    /// there is probably more to do, so it may immediately sweep again rather than waiting out the interval.
    /// </summary>
    /// <remarks>
    /// Implementations must be safe to run concurrently from several nodes — Wolverine's sweeper runs on
    /// every node rather than electing a leader, so two hosts can issue overlapping sweeps against the same
    /// backend. Deleting an already-deleted payload is not an error.
    /// </remarks>
    /// <param name="cutoff">Payloads stored strictly before this moment are eligible for deletion.</param>
    /// <param name="maxCount">Upper bound on the number of payloads to delete in this pass.</param>
    /// <param name="cancellationToken">Token that is cancelled when the host is shutting down.</param>
    Task<int> DeleteExpiredPayloadsAsync(
        DateTimeOffset cutoff,
        int maxCount,
        CancellationToken cancellationToken = default);
}
