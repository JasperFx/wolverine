namespace Wolverine;

/// <summary>
/// One registered recurring message: a named <see cref="CronSchedule" /> plus the factory that
/// builds the message for each occurrence. Registered through <c>opts.Schedules</c>; executed by
/// Wolverine's recurring-message agent, which keeps exactly the NEXT occurrence pre-scheduled
/// through the existing scheduled-message machinery.
/// </summary>
public sealed class RecurringMessage
{
    /// <summary>
    /// The envelope header carrying the schedule name on every occurrence this schedule publishes.
    /// Round-trips on every transport like any other header, and is what stamps the
    /// <c>wolverine.schedule.name</c> tag onto the handler's OpenTelemetry activity.
    /// </summary>
    public const string HeaderKey = "recurring-schedule";

    internal RecurringMessage(string name, CronSchedule schedule, Type messageType,
        Func<DateTimeOffset, object> creator)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A recurring message needs a non-empty name", nameof(name));
        }

        Name = name;
        Schedule = schedule;
        MessageType = messageType;
        Creator = creator;
    }

    /// <summary>
    /// The schedule's identity. Feeds the occurrence deduplication id
    /// (<c>"{Name}:{occurrenceUtc:O}"</c>) and the <see cref="HeaderKey" /> header, so it must be
    /// stable — renaming a schedule makes its in-flight occurrence look unrelated to its next one.
    /// </summary>
    public string Name { get; }

    /// <summary>When this message fires.</summary>
    public CronSchedule Schedule { get; }

    /// <summary>The message type each occurrence publishes.</summary>
    public Type MessageType { get; }

    /// <summary>Builds the message for one occurrence; handed the occurrence time.</summary>
    internal Func<DateTimeOffset, object> Creator { get; }

    /// <summary>
    /// The logical deduplication id for one occurrence of this schedule. Time-zone independent
    /// (the occurrence is normalized to UTC), and deterministic across nodes and restarts — which
    /// is the whole safety story: an agent failover or restart that re-publishes the same
    /// occurrence produces the same id, and the landed GH-4180 deduplication machinery collapses
    /// the duplicate at consumption.
    /// </summary>
    public string DeduplicationIdFor(DateTimeOffset occurrence)
    {
        return $"{Name}:{occurrence.ToUniversalTime():O}";
    }

    public override string ToString()
    {
        return $"{Name}: {MessageType.Name} at {Schedule}";
    }
}
