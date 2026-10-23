using Wolverine.Runtime.Recurring;

namespace Wolverine;

/// <summary>
/// GH-5168. How a recurring schedule's occurrences are produced.
/// </summary>
public enum RecurringExecutionPath
{
    /// <summary>
    /// The single recurring-message agent keeps the NEXT occurrence pre-scheduled as an ordinary
    /// scheduled message, which fires when the scheduled-job poller next runs. The right shape for
    /// long cadences, and the behavior of every schedule before GH-5168.
    /// </summary>
    ScheduledMessages,

    /// <summary>
    /// One agent per schedule waits on an in-memory timer and publishes each occurrence immediately,
    /// with no scheduled inbox row and no poller in between. Chosen automatically for schedules
    /// whose minimum gap is under <see cref="RecurringMessageCollection.TimerThreshold" />.
    /// </summary>
    Timer
}

/// <summary>
/// One registered recurring message: a named <see cref="CronSchedule" /> plus the factory that
/// builds the message for each occurrence. Registered through <c>opts.Schedules</c>. Long-cadence
/// schedules are executed by Wolverine's recurring-message agent, which keeps exactly the NEXT
/// occurrence pre-scheduled through the existing scheduled-message machinery; short-cadence
/// schedules run on a timer agent of their own (see <see cref="ExecutionPath" />).
/// </summary>
public sealed class RecurringMessage
{
    /// <summary>
    /// The envelope header carrying the schedule name on every occurrence this schedule publishes.
    /// Round-trips on every transport like any other header, and is what stamps the
    /// <c>wolverine.schedule.name</c> tag onto the handler's OpenTelemetry activity.
    /// </summary>
    public const string HeaderKey = "recurring-schedule";

    /// <summary>
    /// The envelope header carrying the occurrence instant — which scheduled firing this envelope is —
    /// on every occurrence this schedule publishes. Always UTC, written round-trippable ("O") so it
    /// parses back with <see cref="DateTimeOffset.Parse(string)" />; stamps the
    /// <c>wolverine.schedule.occurrence</c> tag onto the handler's OpenTelemetry activity.
    /// <para>
    /// This exists because <see cref="Envelope.ScheduledTime" /> is cleared by the scheduled
    /// machinery at fire time, so by the time a handler sees the occurrence the instant is gone from
    /// the envelope — recoverable only by string-parsing the deduplication id, which exists for an
    /// unrelated purpose.
    /// </para>
    /// </summary>
    public const string OccurrenceHeaderKey = "recurring-occurrence";

    private readonly RecurringMessageCollection? _parent;
    private (TimeSpan Threshold, RecurringExecutionPath Path)? _classified;

    internal RecurringMessage(string name, CronSchedule schedule, Type messageType,
        Func<DateTimeOffset, object> creator, RecurringMessageCollection? parent = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A recurring message needs a non-empty name", nameof(name));
        }

        Name = name;
        Schedule = schedule;
        MessageType = messageType;
        Creator = creator;
        _parent = parent;
    }

    /// <summary>
    /// GH-5168. The explicit execution path set with <see cref="UseTimer" /> or
    /// <see cref="UseScheduledMessages" />, or null when the path is chosen automatically.
    /// </summary>
    public RecurringExecutionPath? ExecutionPathOverride { get; private set; }

    /// <summary>
    /// GH-5168. How this schedule's occurrences are produced. Unless overridden, a schedule whose
    /// minimum gap between occurrences over one week is under
    /// <see cref="RecurringMessageCollection.TimerThreshold" /> (15 minutes by default) runs on a
    /// timer agent of its own; every other schedule is pre-scheduled through the scheduled-message
    /// machinery exactly as before.
    /// </summary>
    public RecurringExecutionPath ExecutionPath
    {
        get
        {
            if (ExecutionPathOverride.HasValue) return ExecutionPathOverride.Value;

            var threshold = _parent?.TimerThreshold ?? RecurringMessageCollection.DefaultTimerThreshold;
            if (_classified is { } cached && cached.Threshold == threshold) return cached.Path;

            var path = ClassifyByCadence(Schedule, threshold);
            _classified = (threshold, path);
            return path;
        }
    }

    /// <summary>
    /// GH-5168. The automatic classification: <see cref="RecurringExecutionPath.Timer" /> when the
    /// schedule's minimum gap over one week of occurrences is under <paramref name="threshold" />.
    /// </summary>
    internal static RecurringExecutionPath ClassifyByCadence(CronSchedule schedule, TimeSpan threshold)
    {
        if (threshold <= TimeSpan.Zero) return RecurringExecutionPath.ScheduledMessages;

        var gap = schedule.MinimumGapOverAWeek(threshold);
        return gap.HasValue && gap.Value < threshold
            ? RecurringExecutionPath.Timer
            : RecurringExecutionPath.ScheduledMessages;
    }

    /// <summary>
    /// GH-5168. Always run this schedule on its own timer agent, whatever its cadence. Each
    /// occurrence is published the moment it comes due, with no scheduled inbox row.
    /// </summary>
    public RecurringMessage UseTimer()
    {
        ExecutionPathOverride = RecurringExecutionPath.Timer;
        return this;
    }

    /// <summary>
    /// GH-5168. Always pre-schedule this schedule's next occurrence through the scheduled-message
    /// machinery, whatever its cadence -- the behavior of every schedule before GH-5168.
    /// </summary>
    public RecurringMessage UseScheduledMessages()
    {
        ExecutionPathOverride = RecurringExecutionPath.ScheduledMessages;
        return this;
    }

    /// <summary>
    /// GH-5168. The agent URI this schedule runs under when <see cref="ExecutionPath" /> is
    /// <see cref="RecurringExecutionPath.Timer" />: <c>wolverine-recurring-timer:///{name}</c> with the
    /// name URL-encoded. Null for a schedule on the scheduled-message path.
    /// </summary>
    public Uri? TimerAgentUri => ExecutionPath == RecurringExecutionPath.Timer
        ? RecurringTimerAgentFamily.UriFor(Name)
        : null;

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

    /// <summary>
    /// GH-4446. The deduplication id for a MANUAL occurrence — one an operator asked for through
    /// <see cref="IRecurringScheduleControl.TriggerAsync" /> rather than one the cron produced.
    /// <para>
    /// Deliberately distinct from <see cref="DeduplicationIdFor" />: a "run now" issued in the same
    /// instant as a scheduled firing is a separate intent, and collapsing the two would make the
    /// trigger silently do nothing. Still derived from the request instant rather than from a
    /// random value, so it keeps the property the scheduled id has — an agent failover that
    /// re-publishes the same outstanding trigger produces the same id and cannot double-handle it.
    /// </para>
    /// </summary>
    public string ManualDeduplicationIdFor(DateTimeOffset requestedAt)
    {
        return $"{Name}:manual:{requestedAt.ToUniversalTime():O}";
    }

    public override string ToString()
    {
        return $"{Name}: {MessageType.Name} at {Schedule}";
    }
}
