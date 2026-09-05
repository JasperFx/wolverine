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
/// Runtime pause/resume/query surface for the recurring (cron) messages registered through
/// <c>opts.Schedules</c> — the only runtime-mutable state the feature has (schedule definitions
/// stay code-first). Resolve it from the container; it is registered alongside the first schedule.
///
/// <para>
/// Pausing marks the schedule's durable tracking row AND eagerly cancels the pre-scheduled next
/// occurrence, so nothing fires in the gap before the agent's next tick — the caller may be on a
/// different node than the agent. Resuming clears the mark; the agent then pre-schedules the
/// occurrence strictly after the resume time, never back-filling the paused window.
/// </para>
///
/// <para>
/// On a message store without the recurring tracking extension the durable half is unavailable:
/// pause/resume reach only the agent instance in THIS process (effective on a single-node or
/// storeless host, where the agent is always local), are lost on restart, and an
/// already-pre-scheduled occurrence cannot be cancelled — it will still fire once.
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
    /// The durable tracking rows — which schedule owns which pending envelope, next fire times,
    /// pause state. Empty on a store without the recurring tracking extension.
    /// </summary>
    Task<IReadOnlyList<RecurringMessageRecord>> QueryAsync(CancellationToken token = default);
}

internal class RecurringScheduleControl : IRecurringScheduleControl
{
    private readonly IWolverineRuntime _runtime;
    private readonly RecurringMessageAgent? _agent;

    public RecurringScheduleControl(IWolverineRuntime runtime, IEnumerable<IAgentFamily> agentFamilies)
    {
        _runtime = runtime;

        // The singular agent is a container singleton on EVERY node; it only RUNS on one. The
        // local instance is the pause/resume channel exactly when the store cannot be — see below.
        _agent = agentFamilies.OfType<RecurringMessageAgent>().FirstOrDefault();
    }

    public async Task PauseAsync(string name, CancellationToken token = default)
    {
        assertKnown(name);

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
        assertKnown(name);

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

    public Task<IReadOnlyList<RecurringMessageRecord>> QueryAsync(CancellationToken token = default)
    {
        return _runtime.Storage.RecurringMessages.LoadAllAsync(token);
    }

    private void assertKnown(string name)
    {
        if (_runtime.Options.Schedules.FindByName(name) == null)
        {
            throw new UnknownRecurringScheduleException(name, _runtime.Options.Schedules);
        }
    }
}
