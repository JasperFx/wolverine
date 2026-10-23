using JasperFx;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Agents;

namespace Wolverine.Runtime.Recurring;

/// <summary>
/// Thrown when a pause/resume names a recurring schedule this application never registered.
/// Registration is code-first, so an unknown name is a programming (or operator-typo) error —
/// silently no-op'ing it would report success for a schedule that does not exist.
/// </summary>
public class UnknownRecurringScheduleException : Exception
{
    public UnknownRecurringScheduleException(string name, IEnumerable<RecurringMessage> known)
        : base($"No recurring message named '{name}' is registered on this application. " +
               $"Registered schedule(s): {string.Join(", ", known.Select(x => x.Name).DefaultIfEmpty("none"))}")
    {
    }
}

/// <summary>
/// GH-4446. Thrown when a manual trigger names a schedule that is administratively paused. Pausing
/// says the schedule must not fire, so a "run now" is refused rather than allowed to override it —
/// resume the schedule first if you meant to let it run.
/// </summary>
public class RecurringSchedulePausedException : Exception
{
    public RecurringSchedulePausedException(string name)
        : base($"The recurring message schedule '{name}' is paused and cannot be triggered. " +
               "Pausing a schedule means it must not fire; resume it first if you want it to run.")
    {
    }
}

/// <summary>
/// Runtime pause/resume/trigger/query surface for the recurring (cron) messages registered through
/// <c>opts.Schedules</c> — the only runtime-mutable state the feature has (schedule definitions
/// stay code-first). Resolve it from the container; it is registered alongside the first schedule.
///
/// <para>
/// Pausing marks the schedule's durable tracking row AND eagerly cancels the pre-scheduled next
/// occurrence, so nothing fires in the gap before the agent's next tick — the caller may be on a
/// different node than the agent. Resuming clears the mark; the agent then pre-schedules the
/// occurrence strictly after the resume time, never back-filling the paused window. Triggering
/// records a request on the same row for the agent to act on, for the same cross-node reason.
/// </para>
///
/// <para>
/// On a message store without the recurring tracking extension the durable half is unavailable:
/// pause/resume/trigger reach only the agent instance in THIS process (effective on a single-node
/// or storeless host, where the agent is always local), are lost on restart, and an
/// already-pre-scheduled occurrence cannot be cancelled — it will still fire once.
/// </para>
///
/// <para>
/// GH-5168. For a schedule on the timer path (<see cref="RecurringExecutionPath.Timer" />) the same
/// verbs act on its agent instead: pausing applies an <see cref="AgentRestrictionType.Paused" />
/// restriction (durable, cluster-wide, and confirmed before the call returns) and mirrors the pause
/// onto the tracking row; resuming lifts both; triggering publishes one manual occurrence straight
/// from the calling node. A timer schedule whose agent was paused directly is reported paused here
/// too, and refuses a trigger. With no message store, pause and resume stop and start the timer
/// agent in this process.
/// </para>
/// </summary>
public interface IRecurringScheduleControl
{
    /// <summary>
    /// Pause a registered schedule and eagerly cancel its pending pre-scheduled occurrence.
    /// Idempotent; throws <see cref="UnknownRecurringScheduleException" /> for a name this
    /// application never registered.
    /// </summary>
    Task PauseAsync(string name, CancellationToken token = default);

    /// <summary>
    /// Resume a paused schedule. The next occurrence is computed strictly after now — the paused
    /// window is never back-filled. Idempotent; throws
    /// <see cref="UnknownRecurringScheduleException" /> for an unregistered name.
    /// </summary>
    Task ResumeAsync(string name, CancellationToken token = default);

    /// <summary>
    /// GH-4446. Run a registered schedule once, now, out of band — without disturbing its cron
    /// cadence or its pending next occurrence.
    ///
    /// <para>
    /// The request is recorded on the schedule's durable row and the agent publishes one occurrence
    /// for it on its next tick, so this works from any node. The manual occurrence carries its own
    /// deduplication id (<see cref="RecurringMessage.ManualDeduplicationIdFor" />) and is therefore
    /// never collapsed into a scheduled firing. Only one trigger can be outstanding at a time: two
    /// calls landing between two ticks coalesce into a single run.
    /// </para>
    ///
    /// Throws <see cref="UnknownRecurringScheduleException" /> for an unregistered name, and
    /// <see cref="RecurringSchedulePausedException" /> when the schedule is paused.
    /// </summary>
    Task TriggerAsync(string name, CancellationToken token = default);

    /// <summary>
    /// The durable tracking rows — which schedule owns which pending envelope, next fire times,
    /// pause state. Empty on a store without the recurring tracking extension.
    /// </summary>
    Task<IReadOnlyList<RecurringMessageRecord>> QueryAsync(CancellationToken token = default);
}

internal class RecurringScheduleControl : IRecurringScheduleControl
{
    private readonly IWolverineRuntime _runtime;
    private readonly RecurringMessageAgent? _agent;
    private readonly RecurringTimerAgentFamily? _timers;
    private readonly Lazy<RecurringOccurrencePublisher> _publisher;

    public RecurringScheduleControl(IWolverineRuntime runtime, IEnumerable<IAgentFamily> agentFamilies)
    {
        _runtime = runtime;

        var families = agentFamilies.ToArray();

        // The singular agent is a container singleton on EVERY node; it only RUNS on one. The
        // local instance is the pause/resume channel exactly when the store cannot be — see below.
        _agent = families.OfType<RecurringMessageAgent>().FirstOrDefault();
        _timers = families.OfType<RecurringTimerAgentFamily>().FirstOrDefault();

        _publisher = new Lazy<RecurringOccurrencePublisher>(() =>
            new RecurringOccurrencePublisher(runtime, runtime.LoggerFactory.CreateLogger<RecurringScheduleControl>()));
    }

    public async Task PauseAsync(string name, CancellationToken token = default)
    {
        var message = assertKnown(name);

        if (message.ExecutionPath == RecurringExecutionPath.Timer)
        {
            await pauseTimerAsync(message, token);
            return;
        }

        var store = _runtime.Storage.RecurringMessages;
        if (store.Enabled)
        {
            // The durable row is the single channel: the agent reads pause flags off it every
            // tick, whichever node it runs on. Deliberately NOT also marking the local agent —
            // a local mark that a resume on another node cannot clear would pause the schedule
            // forever.
            await store.PauseAsync(name, DateTimeOffset.UtcNow, token);
        }
        else
        {
            // No durable channel — agent memory is all there is. Only effective when the running
            // agent is in this process, and the already-pre-scheduled occurrence still fires.
            _agent?.MarkPaused(name);
        }
    }

    public async Task ResumeAsync(string name, CancellationToken token = default)
    {
        var message = assertKnown(name);

        if (message.ExecutionPath == RecurringExecutionPath.Timer)
        {
            await resumeTimerAsync(message, token);
            return;
        }

        var store = _runtime.Storage.RecurringMessages;
        if (store.Enabled)
        {
            await store.ResumeAsync(name, token);
        }
        else
        {
            _agent?.MarkResumed(name);
        }
    }

    public async Task TriggerAsync(string name, CancellationToken token = default)
    {
        var message = assertKnown(name);

        if (message.ExecutionPath == RecurringExecutionPath.Timer)
        {
            await triggerTimerAsync(message, token);
            return;
        }

        var store = _runtime.Storage.RecurringMessages;
        if (store.Enabled)
        {
            // Same durable channel as pause, for the same reason: the agent is usually on another
            // node. A false answer here means the row says paused — the one refusal this verb has.
            if (!await store.RequestTriggerAsync(name, DateTimeOffset.UtcNow, token))
            {
                throw new RecurringSchedulePausedException(name);
            }
        }
        else if (_agent != null && !_agent.TryMarkTriggered(name))
        {
            throw new RecurringSchedulePausedException(name);
        }
    }

    public async Task<IReadOnlyList<RecurringMessageRecord>> QueryAsync(CancellationToken token = default)
    {
        var rows = await _runtime.Storage.RecurringMessages.LoadAllAsync(token);

        // GH-5168. A timer schedule can be paused straight through its agent (an agents screen, an
        // operator's own ApplyRestrictionsAsync) without the row hearing about it. Report it paused
        // either way, so there is one visible state.
        var pausedTimers = await PausedTimerScheduleNamesAsync(token);
        if (pausedTimers.Count == 0) return rows;

        return rows.Select(row => row.Paused || !pausedTimers.Contains(row.Name)
            ? row
            : new RecurringMessageRecord
            {
                Name = row.Name,
                CronExpression = row.CronExpression,
                EnvelopeIds = row.EnvelopeIds,
                DeduplicationId = row.DeduplicationId,
                NextOccurrence = row.NextOccurrence,
                Paused = true,
                PausedAt = row.PausedAt,
                TriggerRequestedAt = row.TriggerRequestedAt,
                LastUpdated = row.LastUpdated
            }).ToList();
    }

    /// <summary>
    /// GH-5168. The timer schedules currently paused by an agent restriction (or, with no message
    /// store, by the in-memory pause), whatever their tracking rows say.
    /// </summary>
    internal async Task<HashSet<string>> PausedTimerScheduleNamesAsync(CancellationToken token)
    {
        var timerSchedules = _runtime.Options.Schedules.TimerSchedules();
        if (timerSchedules.Count == 0) return [];

        if (isStoreless)
        {
            return timerSchedules.Where(x => _timers?.IsLocallyPaused(x.Name) == true).Select(x => x.Name)
                .ToHashSet();
        }

        var state = await _runtime.Storage.Nodes.LoadNodeAgentStateAsync(token);
        var paused = state.Restrictions.FindPausedAgentUris().ToHashSet();

        return timerSchedules.Where(x => paused.Contains(RecurringTimerAgentFamily.UriFor(x.Name)))
            .Select(x => x.Name).ToHashSet();
    }

    private bool isStoreless => _runtime.Storage is NullMessageStore;

    /// <summary>
    /// GH-5168. A timer schedule pauses by pausing its agent: an agent restriction, which is durable,
    /// cluster-wide and confirmed (the leader stops the agent wherever it runs before
    /// ApplyRestrictionsAsync returns). The restriction goes first so the agent is already stopped
    /// when the tracking row is marked; the row is the mirror that keeps one visible pause state.
    /// </summary>
    private async Task pauseTimerAsync(RecurringMessage message, CancellationToken token)
    {
        var uri = RecurringTimerAgentFamily.UriFor(message.Name);

        if (isStoreless)
        {
            // No restrictions without a store: an in-memory pause of the agent in this process,
            // which is the only place it can be running on such a host.
            _timers?.MarkLocallyPaused(message.Name);
            if (_timers != null)
            {
                var agent = _timers.AgentFor(uri);
                if (agent.Status == AgentStatus.Running) await agent.StopAsync(token);
            }

            return;
        }

        var restrictions = new AgentRestrictions([]);
        restrictions.PauseAgent(uri);
        await _runtime.Agents.ApplyRestrictionsAsync(restrictions, token);

        // Solo mode has no assignment grid to carry a pause out to a running agent (GH-4672 only
        // keeps the Solo loop from restarting it), so stop it here, where it runs.
        if (_runtime.Options.Durability.Mode == DurabilityMode.Solo)
        {
            await _runtime.Agents.StopLocallyAsync(uri);
        }

        var store = _runtime.Storage.RecurringMessages;
        if (store.Enabled)
        {
            await store.PauseAsync(message.Name, DateTimeOffset.UtcNow, token);
        }
    }

    /// <summary>
    /// GH-5168. The mirror image of <see cref="pauseTimerAsync" />, in the opposite order: the row's
    /// pause mark is cleared first, because a starting timer agent reads it and would otherwise sit
    /// out one <see cref="RecurringTimerAgent.PauseCheckInterval" /> before noticing the resume.
    /// </summary>
    private async Task resumeTimerAsync(RecurringMessage message, CancellationToken token)
    {
        var uri = RecurringTimerAgentFamily.UriFor(message.Name);

        if (isStoreless)
        {
            _timers?.MarkLocallyResumed(message.Name);
            if (_timers != null)
            {
                await _timers.AgentFor(uri).StartAsync(token);
            }

            return;
        }

        var store = _runtime.Storage.RecurringMessages;
        if (store.Enabled)
        {
            await store.ResumeAsync(message.Name, token);
        }

        // Only an existing Paused entry can be flipped back, and merging a "None" entry also clears a
        // pin on the same agent -- so carry any pin through the same change, after the None, to keep
        // an operator's placement intact.
        var state = await _runtime.Storage.Nodes.LoadNodeAgentStateAsync(token);
        var pins = state.Restrictions.FindPinnedAgents().Where(x => x.AgentUri == uri).ToArray();

        var changes = new List<AgentRestriction>
        {
            new(Guid.NewGuid(), uri, AgentRestrictionType.None, 0)
        };
        changes.AddRange(pins);

        await _runtime.Agents.ApplyRestrictionsAsync(new AgentRestrictions(changes.ToArray()), token);

        // Solo mode's loop would start it again within a CheckAssignmentPeriod; do it now.
        if (_runtime.Options.Durability.Mode == DurabilityMode.Solo)
        {
            await _runtime.Agents.StartLocallyAsync(uri);
        }
    }

    /// <summary>
    /// GH-5168. A timer schedule has no pre-scheduled occurrence and no tick for a durable trigger
    /// request to wait for, so the manual occurrence is published straight from the calling node,
    /// with the same manual deduplication id the scheduled path uses.
    /// </summary>
    private async Task triggerTimerAsync(RecurringMessage message, CancellationToken token)
    {
        if ((await PausedTimerScheduleNamesAsync(token)).Contains(message.Name))
        {
            throw new RecurringSchedulePausedException(message.Name);
        }

        var store = _runtime.Storage.RecurringMessages;
        if (store.Enabled)
        {
            var row = await store.LoadAsync(message.Name, token);
            if (row is { Paused: true }) throw new RecurringSchedulePausedException(message.Name);
        }

        await _publisher.Value.PublishManualOccurrenceAsync(message, DateTimeOffset.UtcNow);
    }

    private RecurringMessage assertKnown(string name)
    {
        return _runtime.Options.Schedules.FindByName(name)
               ?? throw new UnknownRecurringScheduleException(name, _runtime.Options.Schedules);
    }
}
