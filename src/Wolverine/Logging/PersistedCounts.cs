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
        Outgoing += other.Outgoing;
        Handled += other.Handled;
        DeadLetter += other.DeadLetter;
    }

    public override string ToString()
    {
        return
            $"{nameof(Incoming)}: {Incoming}, {nameof(Scheduled)}: {Scheduled}, {nameof(Outgoing)}: {Outgoing}, {nameof(Handled)}: {Handled}, {nameof(DeadLetter)}: {DeadLetter}";
    }
}