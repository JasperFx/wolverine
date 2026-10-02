using Wolverine.Persistence.Durability;
using Wolverine.RDBMS.Deduplication;

namespace Wolverine.RDBMS;

// GH-4742. The message store is the IReplayableDeduplicationStore, so finding one needs no new member on
// IMessageStore. Explicit, to keep these off MessageDatabase's own surface.
public abstract partial class MessageDatabase<T> : IReplayableDeduplicationStore
{
    private RdbmsReplayableDeduplicationStore? _deduplicatedResponses;

    /// <summary>
    /// GH-4742. Provider hook for a BOUNDED delete of expired <c>[DeduplicatedWithResponse]</c> claims, as
    /// <see cref="BatchedDeleteExpiredDeduplicationClaimsSql" />. Null falls back to one unbounded statement.
    /// </summary>
    public virtual string? BatchedDeleteExpiredDeduplicatedResponsesSql(int batchSize) => null;

    private RdbmsReplayableDeduplicationStore buildDeduplicatedResponseStore()
        => new(_dataSource, QuotedTableNameFor(DatabaseConstants.DeduplicatedResponsesTableName),
            IsUniqueConstraintViolation, BatchedDeleteExpiredDeduplicatedResponsesSql,
            Durability.DeduplicationCleanupBatchSize);

    private RdbmsReplayableDeduplicationStore deduplicatedResponses => _deduplicatedResponses
        ?? throw new InvalidOperationException(
            $"[DeduplicatedWithResponse] needs {nameof(DurabilitySettings)}.{nameof(DurabilitySettings.EnableDeduplicatedResponses)} = true. See GH-4742");

    bool IReplayableDeduplicationStore.Enabled => _deduplicatedResponses != null;

    Task<bool> IReplayableDeduplicationStore.TryClaimAsync(string deduplicationId, string fingerprint,
        string claimToken, DateTimeOffset expires, CancellationToken cancellation)
        => deduplicatedResponses.TryClaimAsync(deduplicationId, fingerprint, claimToken, expires, cancellation);

    Task<DeduplicatedResponseClaim?> IReplayableDeduplicationStore.FindAsync(string deduplicationId,
        CancellationToken cancellation)
        => deduplicatedResponses.FindAsync(deduplicationId, cancellation);

    Task<bool> IReplayableDeduplicationStore.RecordResponseAsync(string deduplicationId, string claimToken,
        DeduplicatedResponse response, CancellationToken cancellation)
        => deduplicatedResponses.RecordResponseAsync(deduplicationId, claimToken, response, cancellation);

    Task IReplayableDeduplicationStore.ReleaseUnansweredAsync(string deduplicationId, string claimToken,
        CancellationToken cancellation)
        => deduplicatedResponses.ReleaseUnansweredAsync(deduplicationId, claimToken, cancellation);

    Task<int> IReplayableDeduplicationStore.DeleteExpiredAsync(DateTimeOffset utcNow, CancellationToken cancellation)
        => deduplicatedResponses.DeleteExpiredAsync(utcNow, cancellation);
}
