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
        DateTimeOffset expires, CancellationToken cancellation)
        => deduplicatedResponses.TryClaimAsync(deduplicationId, fingerprint, expires, cancellation);

    Task<DeduplicatedResponseClaim?> IReplayableDeduplicationStore.FindAsync(string deduplicationId,
        CancellationToken cancellation)
        => deduplicatedResponses.FindAsync(deduplicationId, cancellation);

    Task IReplayableDeduplicationStore.RecordResponseAsync(string deduplicationId, DeduplicatedResponse response,
        CancellationToken cancellation)
        => deduplicatedResponses.RecordResponseAsync(deduplicationId, response, cancellation);

    Task IReplayableDeduplicationStore.ReleaseUnansweredAsync(string deduplicationId, CancellationToken cancellation)
        => deduplicatedResponses.ReleaseUnansweredAsync(deduplicationId, cancellation);

    Task<int> IReplayableDeduplicationStore.DeleteExpiredAsync(DateTimeOffset utcNow, CancellationToken cancellation)
        => deduplicatedResponses.DeleteExpiredAsync(utcNow, cancellation);
}
