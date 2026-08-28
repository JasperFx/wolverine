namespace Wolverine.Persistence.Durability;

/// <summary>
/// GH-4180. Persistence contract for <b>logical</b> message deduplication — the "has this
/// application-defined intent already been carried out?" check that <see cref="Envelope.Id" />
/// cannot answer.
///
/// <para>
/// This is deliberately a SEPARATE store from the transactional inbox rather than a column on
/// <c>wolverine_incoming_envelopes</c>. Three reasons, in descending order of how expensive they
/// would be to discover later:
/// </para>
///
/// <list type="number">
/// <item>
/// <b>Partitioning.</b> With <see cref="DurabilitySettings.EnableInboxPartitioning" /> the inbox table is
/// <c>PARTITION BY LIST (status)</c>, and PostgreSQL refuses a unique index on a partitioned table
/// unless the index includes every partition-key column. A <c>(deduplication_id, status)</c> index
/// would satisfy the engine but not the guarantee: marking an envelope handled updates
/// <c>status</c>, which MOVES the row between partitions, so the same logical id could exist once as
/// <c>Incoming</c> and once as <c>Handled</c>. The constraint would be structurally incapable of
/// enforcing uniqueness, silently, and only for users who enabled partitioning.
/// </item>
/// <item>
/// <b>Retention.</b> A deduplication marker has to outlive the inbox row it came from. Inbox rows are
/// reaped on <see cref="DurabilitySettings.KeepAfterMessageHandling" />, which defaults to five
/// minutes — long enough to absorb a broker redelivery, nowhere near long enough for
/// "this job runs at 03:00 tonight". Its own table carries its own
/// <see cref="DurabilitySettings.DeduplicationWindow" /> without stretching an unrelated setting.
/// </item>
/// <item>
/// <b>Reshaping cost.</b> <c>wolverine_incoming_envelopes</c> is small, hot, and written by every
/// running node. Adding a column is a migration; changing or dropping one later needs an
/// ACCESS EXCLUSIVE lock that queues behind in-flight inbox writes. Keeping this in its own table
/// means the inbox schema never moves for this feature at all.
/// </item>
/// </list>
///
/// <para>
/// Opt-in via <see cref="DurabilitySettings.EnableMessageDeduplication" /> — when disabled, providers
/// MUST return <see cref="NullDeduplicationStore.Instance" /> and MUST NOT provision the backing
/// table, so existing deployments see no schema migration churn on upgrade.
/// </para>
/// </summary>
public interface IDeduplicationStore
{
    /// <summary>
    /// Is this a real, durable store? <see langword="false" /> for <see cref="NullDeduplicationStore" />.
    /// Code generation consults this so a chain that asked for deduplication against a store that
    /// cannot provide it fails loudly at bootstrap rather than silently passing every message.
    /// </summary>
    bool Enabled => true;

    /// <summary>
    /// Claim <paramref name="deduplicationId" /> for the caller. Returns <see langword="true" /> when the
    /// claim was recorded — i.e. this is the FIRST time the id has been seen — and
    /// <see langword="false" /> when it was already present.
    ///
    /// <para>
    /// Implementations MUST make this atomic against concurrent callers on other nodes. The only
    /// race-free way to answer "has anyone else claimed this?" is to attempt the write and let the
    /// database's unique constraint arbitrate; a <c>SELECT</c> followed by an <c>INSERT</c> is exactly
    /// the check-then-act race this feature exists to close, and it will pass every test and fail in
    /// production under the operator double-click it was built for.
    /// </para>
    /// </summary>
    /// <param name="deduplicationId">The application-defined logical id. Never null or empty.</param>
    /// <param name="expires">
    /// When this claim may be reaped. Callers pass <c>UtcNow + <see cref="DurabilitySettings.DeduplicationWindow" /></c>;
    /// the value is stored rather than computed at read time so that shortening the window in
    /// configuration does not retroactively un-claim ids that were recorded under the longer one.
    /// </param>
    Task<bool> TryClaimAsync(string deduplicationId, DateTimeOffset expires,
        CancellationToken cancellation = default);

    /// <summary>
    /// Release a previously successful claim, so the same logical id may be claimed again.
    ///
    /// <para>
    /// This is the compensating half of <see cref="TryClaimAsync" /> for the NON-transactional case.
    /// When the claim rides inside the handler's ambient transaction, a rollback removes it and this
    /// is never called. When there is no ambient transaction — a Buffered or Inline endpoint, or an
    /// HTTP endpoint without transactional middleware — the claim is already committed by the time
    /// the handler runs, so a handler that throws MUST release it. Otherwise the first failed attempt
    /// permanently poisons that logical id and every retry is discarded as a duplicate: the message
    /// is lost, the guarantee inverted, and nothing in the logs says so.
    /// </para>
    ///
    /// <para>
    /// Idempotent — releasing an id that is not claimed is a no-op rather than an error.
    /// </para>
    /// </summary>
    Task ReleaseAsync(string deduplicationId, CancellationToken cancellation = default);

    /// <summary>
    /// Delete every claim whose expiry has passed. Returns the number of rows removed so the caller
    /// can report reaper progress; a reaper that cannot say how far behind it is turns an unbounded
    /// table into a silent one.
    /// </summary>
    Task<int> DeleteExpiredAsync(DateTimeOffset utcNow, CancellationToken cancellation = default);
}

/// <summary>
/// Default no-op deduplication store. Returned by <see cref="IMessageStore.Deduplication" /> when
/// <see cref="DurabilitySettings.EnableMessageDeduplication" /> is <see langword="false" />, or when
/// the message store has no durable backing at all (<c>NullMessageStore</c>).
///
/// <para>
/// <see cref="TryClaimAsync" /> returns <see langword="true" /> — every id looks new — because the
/// alternative, returning <see langword="false" />, would discard all traffic on a misconfigured
/// host. The real protection against reaching this accidentally is <see cref="Enabled" />, which
/// code generation checks at bootstrap.
/// </para>
/// </summary>
public sealed class NullDeduplicationStore : IDeduplicationStore
{
    public static NullDeduplicationStore Instance { get; } = new();

    public bool Enabled => false;

    public Task<bool> TryClaimAsync(string deduplicationId, DateTimeOffset expires,
        CancellationToken cancellation = default)
        => Task.FromResult(true);

    public Task ReleaseAsync(string deduplicationId, CancellationToken cancellation = default)
        => Task.CompletedTask;

    public Task<int> DeleteExpiredAsync(DateTimeOffset utcNow, CancellationToken cancellation = default)
        => Task.FromResult(0);
}
