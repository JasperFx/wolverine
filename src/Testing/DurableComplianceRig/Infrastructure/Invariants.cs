namespace DurableComplianceRig.Infrastructure;

/// <summary>Where a message the rig sent is supposed to end up.</summary>
public enum Outcome
{
    /// <summary>Its handler's effect committed, and its inbox row is settled.</summary>
    Handled,

    /// <summary>It is in dead letter storage, and nothing it did committed.</summary>
    DeadLettered,

    /// <summary>Discarded by an error policy: no effect, no dead letter, a settled row.</summary>
    Discarded,

    /// <summary>
    /// One copy of an intent several copies share a logical deduplication id with. Which copy runs is not known up
    /// front, so per message this only requires no duplicate effect and a settled row; the copies' effects and dead
    /// letters are judged together, per intent, by the deduplication steps.
    /// </summary>
    Deduplicated
}

/// <summary>
/// The durable inbox's invariants, judged against a <see cref="RigSnapshot" />. Each method answers one question
/// and returns every message that breaks it -- never the first one -- so a failing spec says everything that went
/// wrong in one run.
/// </summary>
public class Invariants(RigSnapshot snapshot, IReadOnlyDictionary<Guid, Outcome> expected)
{
    public RigSnapshot Snapshot { get; } = snapshot;

    private int effectsOf(Guid id) => Snapshot.Ledger.Count(x => x.MessageId == id);
    private int deadLettersOf(Guid id) => Snapshot.DeadLetters.Count(x => x.Id == id);

    /// <summary>Expected handled, and no effect committed and nothing dead lettered: the message is gone.</summary>
    public IReadOnlyList<Guid> Lost() => expected
        .Where(x => x.Value == Outcome.Handled && effectsOf(x.Key) == 0 && deadLettersOf(x.Key) == 0)
        .Select(x => x.Key).ToArray();

    /// <summary>More than one committed effect for one message.</summary>
    public IReadOnlyList<(Guid id, int effects)> DuplicateEffects() => expected.Keys
        .Select(id => (id, effects: effectsOf(id)))
        .Where(x => x.effects > 1).ToArray();

    /// <summary>An effect committed for a message that should have been dead lettered or discarded.</summary>
    public IReadOnlyList<Guid> UnexpectedEffects() => expected
        .Where(x => x.Value is not (Outcome.Handled or Outcome.Deduplicated) && effectsOf(x.Key) > 0)
        .Select(x => x.Key).ToArray();

    /// <summary>Dead lettered when it should not have been, missing when it should, or dead lettered twice.</summary>
    public IReadOnlyList<string> DeadLetterMismatches()
    {
        var wrong = new List<string>();
        foreach (var (id, outcome) in expected.Where(x => x.Value != Outcome.Deduplicated))
        {
            var count = deadLettersOf(id);
            if (outcome == Outcome.DeadLettered && count != 1) wrong.Add($"{id}: {count} dead letter rows, expected 1");
            if (outcome != Outcome.DeadLettered && count > 0) wrong.Add($"{id}: dead lettered, expected {outcome}");
        }

        return wrong;
    }

    /// <summary>
    /// Two runs of the same message's handler overlapping in time, on any nodes. Always a defect, whatever the
    /// store's semantics: at-least-once allows running again, never running at the same time.
    /// </summary>
    public IReadOnlyList<string> ConcurrentExecutions()
    {
        var wrong = new List<string>();
        foreach (var group in Snapshot.Executions.GroupBy(x => x.MessageId).Where(x => x.Count() > 1))
        {
            var runs = group.OrderBy(x => x.StartedAt).ToArray();
            for (var i = 0; i < runs.Length; i++)
            for (var j = i + 1; j < runs.Length; j++)
            {
                if (runs[i].Overlaps(runs[j])) wrong.Add($"{runs[i]} overlaps {runs[j]}");
            }
        }

        return wrong;
    }

    /// <summary>
    /// Inbox rows for the run's messages that are neither Handled nor gone once everything should have settled.
    /// 'Incoming' owned by a live node is the GH-4664/5119/5120 shape: nothing will ever reclaim it.
    /// </summary>
    public IReadOnlyList<InboxRow> UnsettledInboxRows() => Snapshot.Inbox
        .Where(x => x.Status != "Handled" && isOurs(x.Id))
        .ToArray();

    /// <summary>Anything left in the outbox once the run is quiet.</summary>
    public IReadOnlyList<OutgoingRow> OutboxLeftovers() => Snapshot.Outgoing;

    /// <summary>The ledger row landed in a different database than the one its tenant owns.</summary>
    public IReadOnlyList<LedgerRow> MisroutedEffects(Func<string?, string> databaseForTenant) => Snapshot.Ledger
        .Where(x => databaseForTenant(x.Tenant) != x.Database)
        .ToArray();

    /// <summary>An inbox row of one of the run's messages in a different database than the one its tenant owns.</summary>
    public IReadOnlyList<InboxRow> MisroutedInboxRows(Func<Guid, string> databaseForMessage) => Snapshot.Inbox
        .Where(x => isOurs(x.Id) && databaseForMessage(x.Id) != x.Database)
        .ToArray();

    /// <summary>A dead letter of one of the run's messages in a different database than the one its tenant owns.</summary>
    public IReadOnlyList<DeadLetterRow> MisroutedDeadLetters(Func<Guid, string> databaseForMessage) => Snapshot.DeadLetters
        .Where(x => isOurs(x.Id) && databaseForMessage(x.Id) != x.Database)
        .ToArray();

    private bool isOurs(Guid id) => expected.ContainsKey(id);

    /// <summary>Every message has reached its expected outcome, so waiting longer cannot change the verdict.</summary>
    public bool IsSettled() =>
        expected.All(x => x.Value switch
        {
            Outcome.Handled => effectsOf(x.Key) > 0 || deadLettersOf(x.Key) > 0,
            Outcome.DeadLettered => deadLettersOf(x.Key) > 0,
            _ => true
        })
        && !UnsettledInboxRows().Any(x => x.Status == "Incoming");
}
