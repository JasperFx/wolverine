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
/// GH-5320. What a recurring schedule does when an occurrence comes due while the previous one is
/// still being handled.
/// </summary>
public enum RecurringOverlapPolicy
{
    /// <summary>
    /// Publish every occurrence as it comes due, whether or not the previous one has finished. The
    /// behavior of every schedule before GH-5320, and the default.
    /// </summary>
    Allow,

    /// <summary>
    /// Skip any occurrence that comes due while the previous one is still being handled. The timer
    /// agent invokes each occurrence and waits for it instead of publishing it and moving on; see
    /// <see cref="RecurringMessage.SkipWhileRunning" /> for exactly what that means.
    /// </summary>
    SkipWhileRunning
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

            // GH-5320. Only the timer agent can tell whether the previous occurrence is still running
            if (OverlapPolicy == RecurringOverlapPolicy.SkipWhileRunning) return RecurringExecutionPath.Timer;

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
        // GH-5320. A schedule faster than the scheduled-job poller can honour runs on a timer whatever
        // the threshold says -- even a threshold of zero, which would otherwise mean "never".
        var floor = CronSchedule.MinimumScheduledMessagesCadence;
        var gap = schedule.MinimumGapOverAWeek(threshold > floor ? threshold : floor);
        if (gap.HasValue && gap.Value < floor) return RecurringExecutionPath.Timer;

        if (threshold <= TimeSpan.Zero) return RecurringExecutionPath.ScheduledMessages;

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
    /// <para>
    /// GH-5320. Refused for a schedule that fires more often than
    /// <see cref="CronSchedule.MinimumScheduledMessagesCadence" />, which the scheduled-job poller
    /// cannot honour, and for a schedule set to <see cref="SkipWhileRunning" />.
    /// </para>
    /// </summary>
    public RecurringMessage UseScheduledMessages()
    {
        var floor = CronSchedule.MinimumScheduledMessagesCadence;
        var gap = Schedule.MinimumGapOverAWeek(floor);
        if (gap.HasValue && gap.Value < floor)
        {
            throw new InvalidOperationException(
                $"Recurring message '{Name}' ({Schedule}) fires more often than every {floor.TotalSeconds:0} seconds, " +
                $"which the scheduled-message path cannot honour: durable scheduled messages replay on " +
                $"{nameof(DurabilitySettings.ScheduledJobPollingTime)} (5 seconds by default). " +
                "Leave this schedule on its timer agent.");
        }

        if (OverlapPolicy == RecurringOverlapPolicy.SkipWhileRunning)
        {
            throw new InvalidOperationException(
                $"Recurring message '{Name}' is set to {nameof(SkipWhileRunning)}(), which only the timer path " +
                "supports. A pre-scheduled occurrence fires from the scheduled-job poller with nothing " +
                "waiting on the previous one.");
        }

        ExecutionPathOverride = RecurringExecutionPath.ScheduledMessages;
        return this;
    }

    /// <summary>
    /// GH-5320. What this schedule does when an occurrence comes due while the previous one is still
    /// being handled. <see cref="RecurringOverlapPolicy.Allow" /> unless
    /// <see cref="SkipWhileRunning" /> was called.
    /// </summary>
    public RecurringOverlapPolicy OverlapPolicy { get; private set; } = RecurringOverlapPolicy.Allow;

    /// <summary>
    /// GH-5320. Skip any occurrence that comes due while the previous one is still being handled, and
    /// pick the schedule back up with the first occurrence after it finishes. Skipped occurrences are
    /// never back-filled. They are logged at debug level, summarized at information level when the
    /// schedule resumes, and counted on the <c>wolverine-recurring-occurrences-skipped</c> counter
    /// tagged with <c>schedule.name</c>.
    /// <para>
    /// This runs the schedule on its timer agent (implying <see cref="UseTimer" />), and changes how
    /// each occurrence is delivered: the agent <b>invokes</b> the occurrence and waits for it, with the
    /// semantics of <c>IMessageBus.InvokeAsync()</c>, instead of publishing it.
    /// </para>
    /// <list type="bullet">
    /// <item>With a local handler, the occurrence runs inline on the node that owns the schedule's
    /// timer agent. There is one such agent per schedule in the cluster, so occurrences of the schedule
    /// do not overlap anywhere in the cluster -- except for the brief double run while the agent moves
    /// between nodes.</item>
    /// <item>With no local handler, the occurrence goes to the single endpoint it routes to by
    /// request/reply, and the agent waits for the acknowledgement, at most
    /// <see cref="WolverineOptions.DefaultRemoteInvocationTimeout" />. After that timeout the next
    /// occurrence can start even if the remote handler is still running.</item>
    /// <item>Other subscribers do not receive the occurrence, and it does not go through a local queue:
    /// there is no inbox row, no queue-level error policy, no dead letter queue. A failed occurrence is
    /// logged and the next one runs on schedule. A handler that never returns stops the schedule
    /// until it does.</item>
    /// <item>A manual <see cref="IRecurringScheduleControl.TriggerAsync" /> still publishes, and is not
    /// covered by the overlap check.</item>
    /// </list>
    /// Refused for a schedule set to <see cref="UseScheduledMessages" />.
    /// </summary>
    public RecurringMessage SkipWhileRunning()
    {
        if (ExecutionPathOverride == RecurringExecutionPath.ScheduledMessages)
        {
            throw new InvalidOperationException(
                $"Recurring message '{Name}' is set to {nameof(UseScheduledMessages)}(), and " +
                $"{nameof(SkipWhileRunning)}() only works on the timer path. A pre-scheduled occurrence fires " +
                "from the scheduled-job poller with nothing waiting on the previous one.");
        }

        OverlapPolicy = RecurringOverlapPolicy.SkipWhileRunning;
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
