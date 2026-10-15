namespace Wolverine.RDBMS.Durability;

/// <summary>
///     GH-5028. What a relational message store answers so the inbox steps of a
///     <see cref="DurabilityMode.ServerlessWithOutbox" /> durability pass,
///     <see cref="ReleaseScheduledMessagesStep" /> and <see cref="RecoverIncomingMessagesStep" />, can find their
///     rows. Rows whose ids are in <c>excluded</c> already failed in this pass and are skipped
/// </summary>
public interface IDurabilityPassInboxSource
{
    /// <summary>
    ///     Load up to <paramref name="limit" /> <c>Scheduled</c> inbox rows whose execution time is at or before
    ///     <paramref name="now" />, earliest first
    /// </summary>
    Task<IReadOnlyList<Envelope>> LoadDueScheduledAsync(DateTimeOffset now, Guid[] excluded, int limit,
        CancellationToken token);

    /// <summary>
    ///     How many rows <see cref="LoadDueScheduledAsync" /> would still find, ignoring the limit
    /// </summary>
    Task<int> CountDueScheduledAsync(DateTimeOffset now, Guid[] excluded, CancellationToken token);

    /// <summary>
    ///     Load up to <paramref name="limit" /> <c>Incoming</c> inbox rows that no live request is handling. When
    ///     <paramref name="staleBefore" /> has a value (<see cref="DurabilitySettings.InboxStaleTime" /> is set),
    ///     that is every row of any owner whose stale clock is at or before it. Otherwise it is the rows owned by
    ///     any node (<c>owner_id = 0</c>)
    /// </summary>
    Task<IReadOnlyList<Envelope>> LoadRecoverableIncomingAsync(DateTimeOffset? staleBefore, Guid[] excluded,
        int limit, CancellationToken token);

    /// <summary>
    ///     How many rows <see cref="LoadRecoverableIncomingAsync" /> would still find, ignoring the limit
    /// </summary>
    Task<int> CountRecoverableIncomingAsync(DateTimeOffset? staleBefore, Guid[] excluded, CancellationToken token);
}
