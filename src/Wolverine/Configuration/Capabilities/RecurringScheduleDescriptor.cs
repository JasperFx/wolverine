using JasperFx.Descriptors;

namespace Wolverine.Configuration.Capabilities;

/// <summary>
/// Capability shape for one recurring (cron) message schedule registered through
/// <c>opts.Schedules</c>. Surfaced through <see cref="ServiceCapabilities.RecurringSchedules" />
/// as a first-class section — rather than an <c>AdditionalCapabilities</c> entry — so external
/// monitoring tools (CritterWatch in particular) get typed fields, and so a schedule change
/// participates in capability change detection.
/// </summary>
/// <remarks>
/// Definition fields (name, cron, time zone, message type) come from the code-first
/// registrations and are always present. Runtime fields (paused state, the pending next
/// occurrence) come from the message store's recurring tracking extension when it exists; on a
/// store without it, <see cref="Paused" /> is reported false and <see cref="NextOccurrence" />
/// falls back to the computed next firing instant.
/// </remarks>
public class RecurringScheduleDescriptor
{
    public RecurringScheduleDescriptor()
    {
    }

    /// <summary>The schedule's registered name — its stable identity (feeds the occurrence
    /// deduplication id and the <c>wolverine.schedule.name</c> trace tag).</summary>
    public string Name { get; set; } = null!;

    /// <summary>The cron expression, exactly as registered (5- or 6-field grammar).</summary>
    public string CronExpression { get; set; } = null!;

    /// <summary>The IANA/Windows id of the time zone occurrences are computed in ("UTC" by default).</summary>
    public string TimeZoneId { get; set; } = null!;

    /// <summary>The message type each occurrence publishes.</summary>
    public TypeDescriptor MessageType { get; set; } = null!;

    /// <summary>
    /// When the next occurrence fires — the pre-scheduled instant off the durable tracking row
    /// when one exists, otherwise the computed next firing instant. Null while the schedule is
    /// paused, or when a fixed-date schedule has run out of occurrences.
    /// </summary>
    public DateTimeOffset? NextOccurrence { get; set; }

    /// <summary>Is this schedule administratively paused?</summary>
    public bool Paused { get; set; }

    /// <summary>When the schedule was paused. Null while running.</summary>
    public DateTimeOffset? PausedAt { get; set; }
}
