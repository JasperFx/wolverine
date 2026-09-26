namespace Wolverine.Logging;

public class PersistedCounts
{
    /// <summary>
    ///     Number of incoming messages currently persisted in the durable inbox
    /// </summary>
    public int Incoming { get; set; }

    /// <summary>
    ///     Number of scheduled messages persisted by the system
    /// </summary>
    public int Scheduled { get; set; }

    /// <summary>
    ///     Number of scheduled messages whose execution time has ALREADY PASSED — i.e. the ones the
    ///     scheduled-job poller should have moved by now. <c>null</c> when the store does not report it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="Scheduled"/>, and the distinction is the whole point: a scheduled queue
    /// holding envelopes that are not due yet is a scheduled queue doing its job. Only an envelope past
    /// its <c>execution_time</c> is evidence that the poller is not moving them.
    /// </para>
    /// <para>
    /// ⚠️ <c>null</c> means NOT MEASURED, not zero. A store that does not populate it makes the
    /// stuck-scheduled-poller health signal stand down rather than guess — the alternative is what this
    /// property exists to fix, a signal that reads a number it cannot interpret.
    /// </para>
    /// </remarks>
    public int? ScheduledDue { get; set; }

    /// <summary>
    ///     Number of outgoing messages currently persisted in the durable outbox
    /// </summary>
    public int Outgoing { get; set; }

    /// <summary>
    ///     The timestamp of the OLDEST envelope still sitting in the durable outbox — the head of the
    ///     queue. <c>null</c> when the store does not report it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// GH-4499 follow-up. <see cref="Outgoing" /> on its own cannot tell a stuck outbox from a busy one: a
    /// depth of 500 reads the same whether it is the same 500 rows every poll or 500 different ones. The
    /// head's timestamp settles it directly — a draining outbox keeps replacing its oldest row, so this
    /// value advances; a stuck one does not move at all.
    /// </para>
    /// <para>
    /// This exists because <see cref="Handled" /> cannot stand in for the outbox the way it does for the
    /// inbox: a successfully sent outgoing envelope is DELETED rather than marked, so there is no completion
    /// counter to read.
    /// </para>
    /// <para>
    /// ⚠️ <c>null</c> means NOT MEASURED, not "empty outbox". The timestamp column only exists when
    /// <see cref="DurabilitySettings.OutboxStaleTime" /> is set, so a store without it makes the
    /// stuck-outbox health signal stand down rather than guess — the same rule as
    /// <see cref="ScheduledDue" />.
    /// </para>
    /// </remarks>
    public DateTimeOffset? OldestOutgoing { get; set; }

    /// <summary>
    ///     Number of previously handled messages temporarily persisted in the durable inbox for idempotency checks
    /// </summary>
    public int Handled { get; set; }

    /// <summary>
    ///     Number of error messages currently persisted in the dead letter
    /// </summary>
    public int DeadLetter { get; set; }

    public void Add(PersistedCounts other)
    {
        Incoming += other.Incoming;
        Scheduled += other.Scheduled;
        // Unknown + unknown stays unknown; anything measured contributes. A mixed fleet where only
        // some stores report due counts therefore under-reports rather than over-reports, which is the
        // safe direction for a signal that raises an alert.
        ScheduledDue = ScheduledDue is null && other.ScheduledDue is null
            ? null
            : (ScheduledDue ?? 0) + (other.ScheduledDue ?? 0);
        Outgoing += other.Outgoing;

        // The EARLIEST head across the fleet, since that is the one that has waited longest. Unknown +
        // unknown stays unknown, and anything measured contributes -- the same reasoning as ScheduledDue,
        // except that the combining operation for a timestamp is MIN rather than a sum.
        OldestOutgoing = OldestOutgoing is null
            ? other.OldestOutgoing
            : other.OldestOutgoing is null
                ? OldestOutgoing
                : OldestOutgoing < other.OldestOutgoing
                    ? OldestOutgoing
                    : other.OldestOutgoing;

        Handled += other.Handled;
        DeadLetter += other.DeadLetter;
    }

    public Dictionary<string, PersistedCounts> Tenants { get; } = new();

    public override string ToString()
    {
        return
            $"{nameof(Incoming)}: {Incoming}, {nameof(Scheduled)}: {Scheduled}, {nameof(Outgoing)}: {Outgoing}, {nameof(Handled)}: {Handled}, {nameof(DeadLetter)}: {DeadLetter}";
    }
}