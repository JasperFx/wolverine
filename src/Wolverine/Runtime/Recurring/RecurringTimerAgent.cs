using JasperFx;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Agents;

namespace Wolverine.Runtime.Recurring;

/// <summary>
/// GH-5168. The agent behind one short-cadence recurring schedule
/// (<see cref="RecurringExecutionPath.Timer" />). It computes the next occurrence, waits for it on
/// its <see cref="TimeProvider" />, and publishes it immediately -- no scheduled inbox row, no wait
/// for the scheduled-job poller, and no tracking-row write per occurrence.
///
/// <para>
/// One of these runs per timer schedule, placed by Wolverine's agent assignment like any other
/// agent (<see cref="RecurringTimerAgentFamily" />). Each occurrence carries the same deduplication id,
/// headers and per-occurrence activity as the scheduled-message path, so a brief double run during
/// agent reassignment, or a rolling deploy with old nodes still pre-scheduling the same schedule,
/// collapses to one handling per occurrence at consumption.
/// </para>
///
/// <para>
/// Missed occurrences are skipped, never back-filled: a (re)started agent begins with the first
/// occurrence after "now", so the occurrences that fell inside a reassignment window are lost.
/// </para>
///
/// <para>
/// Pausing is normally an agent restriction (the agent is simply not running), but the agent also
/// honours the pause mark on the schedule's durable tracking row, read when it starts and then at
/// most every <see cref="PauseCheckInterval" />. That covers a schedule paused while it still ran on
/// the scheduled-message path, before a release moved it to a timer.
/// </para>
/// </summary>
internal class RecurringTimerAgent : IAgent
{
    private readonly IWolverineRuntime _runtime;
    private readonly ILogger _logger;
    private readonly RecurringOccurrencePublisher _publisher;

    private CancellationTokenSource? _cancellation;
    private Task? _loop;

    /// <summary>Total occurrences this agent instance has published. A test seam only.</summary>
    internal int OccurrencesPublished;

    public RecurringTimerAgent(RecurringMessage message, IWolverineRuntime runtime, ILogger logger)
    {
        Message = message;
        _runtime = runtime;
        _logger = logger;
        _publisher = new RecurringOccurrencePublisher(runtime, logger);
        Uri = RecurringTimerAgentFamily.UriFor(message.Name);
    }

    public RecurringMessage Message { get; }

    public Uri Uri { get; }

    public AgentStatus Status { get; private set; } = AgentStatus.Stopped;

    public string Description =>
        $"Recurring timer for schedule '{Message.Name}' ({Message.Schedule}), publishing {Message.MessageType.Name} as each occurrence comes due";

    /// <summary>
    /// Settable purely as a test seam, matching <see cref="RecurringMessageAgent.TimeProvider" />.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// The longest the agent goes without re-reading the pause mark on the schedule's tracking row,
    /// and so also the longest single wait. Internal so tests can tighten it.
    /// </summary>
    internal TimeSpan PauseCheckInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to wait before retrying an occurrence whose publish failed. The retry stops once the
    /// following occurrence is due -- a failed occurrence is then skipped like any missed one.
    /// </summary>
    internal TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(1);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_loop != null) return Task.CompletedTask;

        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;

        // GH-4647, same as RecurringMessageAgent: the loop must not capture whatever activity was
        // ambient when the agent started, or every occurrence would be parented to it.
        using (ExecutionContext.SuppressFlow())
        {
            _loop = Task.Run(() => runAsync(token), CancellationToken.None);
        }

        Status = AgentStatus.Running;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cancellation != null)
        {
            await _cancellation.CancelAsync();
        }

        if (_loop != null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (TimeoutException)
            {
                // The loop is only ever waiting or publishing; a restart rebuilds everything from
                // the registration.
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cancellation?.Dispose();
        _cancellation = null;
        _loop = null;
        Status = AgentStatus.Stopped;
    }

    private async Task runAsync(CancellationToken cancellation)
    {
        // Same reason as RecurringMessageAgent: the agents start before the transports, and a route
        // computed in that window can be cached empty for the life of the host.
        if (_runtime is WolverineRuntime runtime)
        {
            try
            {
                await runtime.FullyStarted.WaitAsync(cancellation);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        var state = new LoopState();

        while (!cancellation.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                var next = await StepAsync(state, cancellation);
                if (next == null) return; // a fixed-date schedule ran out of occurrences
                delay = next.Value;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error in the recurring timer loop for schedule '{Name}'", Message.Name);
                delay = RetryInterval;
            }

            try
            {
                await Task.Delay(delay, TimeProvider, cancellation);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal class LoopState
    {
        public DateTimeOffset? Pending;
        public DateTimeOffset? NextPauseCheck;
        public bool PausedOnRow;
    }

    /// <summary>
    /// One pass of the loop: publish the pending occurrence when it is due, and answer how long to
    /// wait before the next pass, or null when the schedule has no further occurrences. Factored out
    /// of the loop so tests can drive it against a controlled clock.
    /// </summary>
    internal async Task<TimeSpan?> StepAsync(LoopState state, CancellationToken cancellation)
    {
        var now = TimeProvider.GetUtcNow();

        if (state.NextPauseCheck == null || now >= state.NextPauseCheck)
        {
            state.PausedOnRow = await isPausedOnTheRowAsync(cancellation);
            state.NextPauseCheck = now.Add(PauseCheckInterval);
        }

        if (state.PausedOnRow)
        {
            // Never back-fill the paused window: forget the pending occurrence, so the first one
            // after the resume is computed from the resume instant.
            state.Pending = null;
            return PauseCheckInterval;
        }

        state.Pending ??= Message.Schedule.NextOccurrence(now);
        if (state.Pending == null)
        {
            _logger.LogInformation("Recurring message '{Name}' has no further occurrences", Message.Name);
            return null;
        }

        var pending = state.Pending.Value;
        if (now < pending)
        {
            var wait = pending - now;
            return wait > PauseCheckInterval ? PauseCheckInterval : wait;
        }

        try
        {
            var outgoing = await _publisher.PublishOccurrenceAsync(Message, pending, null);
            if (outgoing.Length > 0)
            {
                Interlocked.Increment(ref OccurrencesPublished);
            }
        }
        catch (Exception e)
        {
            var following = Message.Schedule.NextOccurrence(pending);
            if (following != null && following.Value > TimeProvider.GetUtcNow())
            {
                _logger.LogError(e,
                    "Failed to publish the occurrence of recurring message '{Name}' for {Occurrence}; retrying",
                    Message.Name, pending);
                return RetryInterval;
            }

            _logger.LogError(e,
                "Failed to publish the occurrence of recurring message '{Name}' for {Occurrence}; skipping it",
                Message.Name, pending);
        }

        // Strictly after both the occurrence just handled and now: a late wake-up publishes the one
        // due occurrence and skips anything else it slept through, never back-filling.
        var after = TimeProvider.GetUtcNow();
        if (after < pending) after = pending;
        state.Pending = Message.Schedule.NextOccurrence(after);

        if (state.Pending == null) return null;

        var untilNext = state.Pending.Value - TimeProvider.GetUtcNow();
        if (untilNext < TimeSpan.Zero) untilNext = TimeSpan.Zero;
        return untilNext > PauseCheckInterval ? PauseCheckInterval : untilNext;
    }

    private async Task<bool> isPausedOnTheRowAsync(CancellationToken cancellation)
    {
        var store = _runtime.Storage.RecurringMessages;
        if (!store.Enabled) return false;

        try
        {
            var row = await store.LoadAsync(Message.Name, cancellation);
            return row is { Paused: true };
        }
        catch (Exception e) when (!cancellation.IsCancellationRequested)
        {
            // Delivery does not depend on the row; an unreachable store costs only the row's pause
            // mark until the next check. The agent restriction is the primary pause either way.
            _logger.LogError(e,
                "Unable to read the tracking row of recurring message '{Name}'; continuing without its pause mark",
                Message.Name);
            return false;
        }
    }
}
