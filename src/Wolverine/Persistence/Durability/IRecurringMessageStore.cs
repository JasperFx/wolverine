namespace Wolverine.Persistence.Durability;

/// <summary>
/// One recurring message schedule's durable tracking state — the row behind
/// <see cref="IRecurringMessageStore" />. The scheduled INBOX row is the materialized next
/// occurrence (delivery never runs through this table); this record is the bookkeeping beside it
/// that makes the "next occurrence is up" guarantee verifiable and gives management tooling a
/// stable handle on what is pending for which schedule.
/// </summary>
public class RecurringMessageRecord
{
    /// <summary>The schedule's registered name — the primary key.</summary>
    public required string Name { get; init; }

    /// <summary>The schedule's cron expression, recorded for diagnostics and management surfaces.</summary>
    public string CronExpression { get; init; } = string.Empty;

    /// <summary>
    /// The envelope ids of the pre-scheduled inbox rows for the next occurrence. Usually one;
    /// more when the message type routes to multiple durable subscribers. Empty while the
    /// schedule is paused, before its first publish, or when no route stored durably.
    /// </summary>
    public Guid[] EnvelopeIds { get; init; } = [];

    /// <summary>
    /// The deterministic occurrence deduplication id of the pending occurrence — the stable
    /// secondary key that survives even where an envelope id could not be captured.
    /// </summary>
    public string? DeduplicationId { get; init; }

    /// <summary>When the pending occurrence fires. Null while paused or never published.</summary>
    public DateTimeOffset? NextOccurrence { get; init; }

    /// <summary>Is this schedule administratively paused?</summary>
    public bool Paused { get; init; }

    /// <summary>When the schedule was paused. Null while running.</summary>
    public DateTimeOffset? PausedAt { get; init; }

    /// <summary>Last time the recurring agent (or a pause/resume) touched this row.</summary>
    public DateTimeOffset LastUpdated { get; init; }
}

/// <summary>
/// Message-store extension tracking recurring (cron) message schedules — one row per registered
/// schedule, mapping the schedule name to the envelope id(s) of its pre-scheduled next occurrence
/// plus its pause state. Opt-in exactly like the rest of the recurring feature: registering the
/// first schedule through <c>opts.Schedules</c> is what makes providers build a real
/// implementation and provision the backing table; with zero schedules registered they MUST
/// return <see cref="NullRecurringMessageStore.Instance" /> and leave the schema untouched.
///
/// <para>
/// What the tracking buys, in order of importance: the recurring agent's guarantee becomes
/// <b>verifiable</b> (it can confirm the tracked envelope still sits <c>Scheduled</c> in the inbox
/// and re-publish when something cancelled it out from under the schedule); <b>pause/resume</b>
/// state is durable across restarts and visible to whichever node holds the agent; and management
/// tooling can interrogate which schedule owns which pending envelope without parsing
/// deduplication-id strings.
/// </para>
///
/// <para>
/// Defaulted rather than abstract on <see cref="IMessageStore" /> on purpose (the
/// <see cref="IMessageStore.Deduplication" /> precedent): a provider with no answer inherits the
/// no-op, and the feature degrades gracefully — schedules still fire, verification just cannot
/// see the inbox and pause state is not durable.
/// </para>
/// </summary>
public interface IRecurringMessageStore
{
    /// <summary>
    /// Is this a real, durable store? <see langword="false" /> for
    /// <see cref="NullRecurringMessageStore" />. The recurring agent checks this before it spends
    /// any effort on tracking or verification.
    /// </summary>
    bool Enabled => true;

    /// <summary>
    /// Upsert the tracking row for one schedule after publishing its next occurrence. Overwrites
    /// the publish bookkeeping (cron expression, envelope ids, deduplication id, next occurrence,
    /// last updated) and MUST preserve the row's pause state — the agent's record of a publish is
    /// never permission to un-pause.
    /// </summary>
    Task RecordPublishedAsync(RecurringMessageRecord record, CancellationToken token = default);

    /// <summary>Load one schedule's tracking row. Null when the schedule has never been recorded.</summary>
    Task<RecurringMessageRecord?> LoadAsync(string name, CancellationToken token = default);

    /// <summary>Load every schedule's tracking row.</summary>
    Task<IReadOnlyList<RecurringMessageRecord>> LoadAllAsync(CancellationToken token = default);

    /// <summary>
    /// How many of these envelope ids still sit in the durable inbox with
    /// <see cref="EnvelopeStatus.Scheduled" /> status? The verification half of record-and-verify:
    /// a count short of <paramref name="envelopeIds" />.Length means something cancelled or lost a
    /// pre-scheduled occurrence, and the agent re-publishes it (same occurrence, same
    /// deduplication id, so an envelope that actually fired in the gap still collapses to one
    /// handling).
    /// </summary>
    Task<int> CountStillScheduledAsync(Guid[] envelopeIds, CancellationToken token = default);

    /// <summary>
    /// Pause a schedule: mark its row paused AND eagerly cancel (delete from the inbox) any
    /// tracked pre-scheduled envelopes — the caller may be on a different node than the agent, and
    /// nothing may fire in the gap before the agent's next tick. Idempotent: pausing an
    /// already-paused schedule keeps the original <see cref="RecurringMessageRecord.PausedAt" />
    /// and is a no-op; a schedule with no row yet gets a paused-only row, so pausing before the
    /// first publish works.
    /// </summary>
    Task PauseAsync(string name, DateTimeOffset pausedAt, CancellationToken token = default);

    /// <summary>
    /// Resume a paused schedule: clear the pause mark so the agent's next tick pre-schedules the
    /// occurrence strictly after the resume time. The paused window is never back-filled.
    /// Idempotent — resuming a running or unknown schedule is a no-op.
    /// </summary>
    Task ResumeAsync(string name, CancellationToken token = default);
}

/// <summary>
/// Default no-op recurring-message tracking. Returned by
/// <see cref="IMessageStore.RecurringMessages" /> when no schedules are registered, and by stores
/// with no durable backing for the extension. Recurring messages still work on this — the agent's
/// in-memory picture drives publishing — but verification no-ops (<see cref="CountStillScheduledAsync" />
/// reports everything present so nothing is ever spuriously re-published) and pause state is not
/// durable or cluster-visible.
/// </summary>
public sealed class NullRecurringMessageStore : IRecurringMessageStore
{
    public static NullRecurringMessageStore Instance { get; } = new();

    public bool Enabled => false;

    public Task RecordPublishedAsync(RecurringMessageRecord record, CancellationToken token = default)
        => Task.CompletedTask;

    public Task<RecurringMessageRecord?> LoadAsync(string name, CancellationToken token = default)
        => Task.FromResult<RecurringMessageRecord?>(null);

    public Task<IReadOnlyList<RecurringMessageRecord>> LoadAllAsync(CancellationToken token = default)
        => Task.FromResult<IReadOnlyList<RecurringMessageRecord>>([]);

    public Task<int> CountStillScheduledAsync(Guid[] envelopeIds, CancellationToken token = default)
        => Task.FromResult(envelopeIds.Length);

    public Task PauseAsync(string name, DateTimeOffset pausedAt, CancellationToken token = default)
        => Task.CompletedTask;

    public Task ResumeAsync(string name, CancellationToken token = default)
        => Task.CompletedTask;
}
