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