namespace Wolverine.Persistence.Durability;

/// <summary>
///     GH-5028. What a message store answers so the inbox steps of a
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
    ///     Load up to <paramref name="limit" /> <c>Incoming</c> inbox rows that no live request is handling: the
    ///     rows owned by any node (<c>owner_id = 0</c>) and, when <paramref name="staleBefore" /> has a value
    ///     (<see cref="DurabilitySettings.InboxStaleTime" /> is set), every row of any other owner whose stale clock
    ///     is at or before it too. A store with no inbox stale clock, such as RavenDb or Cosmos DB, takes only the
    ///     rows owned by any node
    /// </summary>
    Task<IReadOnlyList<Envelope>> LoadRecoverableIncomingAsync(DateTimeOffset? staleBefore, Guid[] excluded,
        int limit, CancellationToken token);

    /// <summary>
    ///     How many rows <see cref="LoadRecoverableIncomingAsync" /> would still find, ignoring the limit
    /// </summary>
    Task<int> CountRecoverableIncomingAsync(DateTimeOffset? staleBefore, Guid[] excluded, CancellationToken token);
}
