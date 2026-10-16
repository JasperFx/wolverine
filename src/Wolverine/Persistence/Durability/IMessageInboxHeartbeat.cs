namespace Wolverine.Persistence.Durability;

/// <summary>
/// Optional capability of an <see cref="IMessageInbox" /> that supports
/// <see cref="DurabilitySettings.InboxStaleTime" />: restarting the stale clock of inbox rows a node is still
/// holding in memory -- running in a handler, or waiting in its local queue. Without it, a handler that runs
/// longer than InboxStaleTime has its row released underneath it, recovery hands the row straight back, and a
/// second copy starts while the first is still running.
/// </summary>
/// <remarks>
/// A separate interface rather than a member of <see cref="IMessageInbox" />, so a store that has no stale
/// sweep, or a third-party one, needs no change. The durable receiver only calls it when InboxStaleTime is set.
/// </remarks>
public interface IMessageInboxHeartbeat
{
    /// <summary>
    /// Restart the stale clock of these envelopes' inbox rows, for rows still waiting to run
    /// (<see cref="EnvelopeStatus.Incoming" />) and still owned by <paramref name="ownerId" />. Rows that have
    /// moved on -- handled, rescheduled, released -- are left alone.
    /// </summary>
    Task RestartStaleClockAsync(int ownerId, IReadOnlyList<Envelope> envelopes);
}
