using System.Collections;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>
    /// Schedule a message with a public no-argument constructor on a cron expression. The schedule
    /// name defaults to the message type's name.
    /// </summary>
    public RecurringMessage RecurringMessage<T>(string cronExpression, TimeZoneInfo? timeZone = null)
        where T : new()
    {
        return RecurringMessage<T>(new CronSchedule(cronExpression, timeZone));
    }

    /// <summary>
    /// Schedule a message with a public no-argument constructor on an already-parsed
    /// <see cref="CronSchedule" />. The schedule name defaults to the message type's name.
    /// </summary>
    public RecurringMessage RecurringMessage<T>(CronSchedule schedule) where T : new()
    {
        return register(new RecurringMessage(typeof(T).Name, schedule, typeof(T), _ => new T()!));
    }

    /// <summary>
    /// Schedule a factory-built message on a cron expression. The factory is handed the occurrence
    /// time, so a message can describe the window it covers.
    /// </summary>
    public RecurringMessage RecurringMessage<T>(string name, string cronExpression,
        Func<DateTimeOffset, T> creator, TimeZoneInfo? timeZone = null) where T : class
    {
        return RecurringMessage(name, new CronSchedule(cronExpression, timeZone), creator);
    }

    /// <summary>
    /// Schedule a factory-built message on an already-parsed <see cref="CronSchedule" />.
    /// </summary>
    public RecurringMessage RecurringMessage<T>(string name, CronSchedule schedule,
        Func<DateTimeOffset, T> creator) where T : class
    {
        if (creator == null)
        {
            throw new ArgumentNullException(nameof(creator));
        }

        return register(new RecurringMessage(name, schedule, typeof(T), occurrence => creator(occurrence)));
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
        _parent.Durability.EnableMessageDeduplication = true;

        // The other half of the opt-in: the main message store reads this flag to provision the
        // wolverine_recurring_messages tracking table and build a real IRecurringMessageStore,
        // which is what makes the agent's guarantee verifiable and pause/resume durable.
        _parent.Durability.EnableRecurringMessages = true;

        if (!_agentRegistered)
        {
            _agentRegistered = true;
            _parent.Services.AddSingularAgent<RecurringMessageAgent>();
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
