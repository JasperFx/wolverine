using System.Collections;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Recurring;

namespace Wolverine;

/// <summary>
/// The recurring (cron) message registrations for this application — <c>opts.Schedules</c>.
///
/// <para>
/// Registering the first schedule is the feature's opt-in: it registers the single-per-cluster
/// recurring-message agent and enables the logical message deduplication that makes agent
/// failover/restart double-publishes harmless. A host with zero schedules registered behaves —
/// and migrates its message store — exactly as it did before this feature existed.
/// </para>
///
/// <para>
/// Registration is code-first and configuration-time only. A bad cron expression or a duplicate
/// name throws here, at the line that wrote it, never at startup on some other host.
/// </para>
/// </summary>
public sealed class RecurringMessageCollection : IEnumerable<RecurringMessage>
{
    private readonly WolverineOptions _parent;
    private readonly List<RecurringMessage> _messages = [];
    private bool _agentRegistered;

    internal RecurringMessageCollection(WolverineOptions parent)
    {
        _parent = parent;
    }

    /// <summary>The default for <see cref="TimerThreshold" />: 15 minutes.</summary>
    public static readonly TimeSpan DefaultTimerThreshold = TimeSpan.FromMinutes(15);

    /// <summary>
    /// GH-5168. A schedule whose minimum gap between occurrences over one week is shorter than this
    /// runs on a timer agent of its own (<see cref="RecurringExecutionPath.Timer" />) instead of being
    /// pre-scheduled through the scheduled-message machinery, where every occurrence waits on the
    /// scheduled-job poller and costs an inbox row. 15 minutes by default; set it to
    /// <see cref="TimeSpan.Zero" /> to keep every schedule on the scheduled-message path unless it opts
    /// in with <see cref="RecurringMessage.UseTimer" />. A per-schedule
    /// <see cref="RecurringMessage.UseTimer" /> or <see cref="RecurringMessage.UseScheduledMessages" />
    /// always wins over this setting.
    /// </summary>
    public TimeSpan TimerThreshold { get; set; } = DefaultTimerThreshold;

    /// <summary>GH-5168. The registered schedules that run on a timer agent of their own.</summary>
    internal IReadOnlyList<RecurringMessage> TimerSchedules()
    {
        return _messages.Where(x => x.ExecutionPath == RecurringExecutionPath.Timer).ToArray();
    }

    /// <summary>
    /// Schedule a message with a public no-argument constructor on a cron expression. The schedule
    /// name defaults to the message type's name.
    /// </summary>
    public RecurringMessage ScheduleRecurring<T>(string cronExpression, TimeZoneInfo? timeZone = null)
        where T : new()
    {
        return ScheduleRecurring<T>(new CronSchedule(cronExpression, timeZone));
    }

    /// <summary>
    /// Schedule a message with a public no-argument constructor on an already-parsed
    /// <see cref="CronSchedule" />. The schedule name defaults to the message type's name.
    /// </summary>
    public RecurringMessage ScheduleRecurring<T>(CronSchedule schedule) where T : new()
    {
        return register(new RecurringMessage(typeof(T).Name, schedule, typeof(T), _ => new T()!, this));
    }

    /// <summary>
    /// Schedule a factory-built message on a cron expression. The factory is handed the occurrence
    /// time, so a message can describe the window it covers.
    /// </summary>
    public RecurringMessage ScheduleRecurring<T>(string name, string cronExpression,
        Func<DateTimeOffset, T> creator, TimeZoneInfo? timeZone = null) where T : class
    {
        return ScheduleRecurring(name, new CronSchedule(cronExpression, timeZone), creator);
    }

    /// <summary>
    /// Schedule a factory-built message on an already-parsed <see cref="CronSchedule" />.
    /// </summary>
    public RecurringMessage ScheduleRecurring<T>(string name, CronSchedule schedule,
        Func<DateTimeOffset, T> creator) where T : class
    {
        if (creator == null)
        {
            throw new ArgumentNullException(nameof(creator));
        }

        return register(new RecurringMessage(name, schedule, typeof(T), occurrence => creator(occurrence), this));
    }

    /// <summary>Look up a registered schedule by name. Null when none matches.</summary>
    public RecurringMessage? FindByName(string name)
    {
        return _messages.FirstOrDefault(x => x.Name == name);
    }

    /// <summary>The message types with at least one registered schedule.</summary>
    internal IReadOnlyList<Type> MessageTypes()
    {
        return _messages.Select(x => x.MessageType).Distinct().ToArray();
    }

    public bool Any()
    {
        return _messages.Count != 0;
    }

    public int Count => _messages.Count;

    private RecurringMessage register(RecurringMessage message)
    {
        if (_messages.Any(x => x.Name == message.Name))
        {
            throw new ArgumentException(
                $"A recurring message named '{message.Name}' is already registered. Names are " +
                "identities (they feed the occurrence deduplication id and the OpenTelemetry tag) — " +
                "give this schedule an explicit, distinct name.");
        }

        _messages.Add(message);

        // The occurrence dedup id is only protective if something consumes it: turning the
        // GH-4180 machinery on and pointing it at exactly the cron-scheduled message types is
        // part of the registration, never a documented prerequisite the user has to remember.
        // (Hosts whose store cannot back it get a startup warning, not a broken boot — the
        // failover double-fire window then matches the pre-deduplication status quo.)
        // GH-4757: promoted from None only. An application that explicitly chose CompareByString keeps
        // it -- registering a schedule is not the moment to silently reshape its deduplication table.
        if (_parent.Durability.MessageDeduplicationMode == MessageDeduplicationMode.None)
        {
            _parent.Durability.MessageDeduplicationMode = MessageDeduplicationMode.CompareByHash;
        }

        // The other half of the opt-in: the main message store reads this flag to provision the
        // wolverine_recurring_messages tracking table and build a real IRecurringMessageStore,
        // which is what makes the agent's guarantee verifiable and pause/resume durable.
        _parent.Durability.EnableRecurringMessages = true;

        if (!_agentRegistered)
        {
            _agentRegistered = true;
            _parent.Services.AddSingularAgent<RecurringMessageAgent>();

            // GH-5168. Registered with the first schedule whatever its cadence, because which schedules
            // run on a timer is only known once configuration is finished (TimerThreshold and the
            // per-schedule overrides can be set after the registration call). A family with no timer
            // schedules simply knows no agents.
            _parent.Services.AddSingleton<IAgentFamily, RecurringTimerAgentFamily>();
            _parent.Services.AddSingleton<IRecurringScheduleControl, RecurringScheduleControl>();
            _parent.RegisteredPolicies.Add(new RecurringDeduplicationPolicy(this));
        }

        return message;
    }

    public IEnumerator<RecurringMessage> GetEnumerator()
    {
        return _messages.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
