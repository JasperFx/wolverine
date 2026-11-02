using System.Diagnostics.Metrics;
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
///
/// <para>
/// GH-5320. Occurrences are always computed from the cron, strictly after the occurrence just handled
/// and "now", never as "now + interval", so a one second schedule does not drift however late the
/// timer wakes. For a <see cref="RecurringOverlapPolicy.SkipWhileRunning" /> schedule the agent invokes
/// each occurrence in the background and skips any occurrence that comes due before it finishes.
/// </para>
/// </summary>
internal class RecurringTimerAgent : IAgent
{
    private readonly IWolverineRuntime _runtime;
    private readonly ILogger _logger;
    private readonly RecurringOccurrencePublisher _publisher;
    private readonly Counter<int>? _skippedCounter;

    private CancellationTokenSource? _cancellation;
    private Task? _loop;

    /// <summary>Total occurrences this agent instance has published. A test seam only.</summary>
    internal int OccurrencesPublished;

    /// <summary>
    /// GH-5320. Total occurrences this agent instance skipped because the previous one was still
    /// running. A test seam only; the metric is <see cref="MetricsConstants.RecurringOccurrencesSkipped" />.
    /// </summary>
    internal int OccurrencesSkipped;

    public RecurringTimerAgent(RecurringMessage message, IWolverineRuntime runtime, ILogger logger,
        Counter<int>? skippedCounter = null)
    {
        Message = message;
        _runtime = runtime;
        _logger = logger;
        _skippedCounter = skippedCounter;
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

        try
        {
            await loopAsync(state, cancellation);
        }
        finally
        {
            // GH-5320. The invocation in flight shares the agent's cancellation token, so it is
            // already being told to stop. Give it the same bounded wait StopAsync gives the loop.
            if (state.Running is { IsCompleted: false } running)
            {
                try
                {
                    await running.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                }
                catch (Exception)
                {
                    // Already logged inside the invocation, or a timeout on shutdown
                }
            }
        }
    }

    private async Task loopAsync(LoopState state, CancellationToken cancellation)
    {
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

        /// <summary>
        /// GH-5320. The occurrence currently being invoked for a
        /// <see cref="RecurringOverlapPolicy.SkipWhileRunning" /> schedule. Null otherwise.
        /// </summary>
        public Task? Running;

        /// <summary>GH-5320. Occurrences skipped since the last one that ran, for the resume log.</summary>
        public int SkippedInARow;
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

        if (Message.OverlapPolicy == RecurringOverlapPolicy.SkipWhileRunning)
        {
            runOrSkip(state, pending, cancellation);
        }
        else
        {
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
                var retry = retryOrSkipFailedPublish(e, pending);
                if (retry != null) return retry;
            }
        }

        return scheduleFollowing(state, pending);
    }

    // The retry wait, or null to skip the failed occurrence and move on to the next one
    private TimeSpan? retryOrSkipFailedPublish(Exception e, DateTimeOffset pending)
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
        return null;
    }

    private TimeSpan? scheduleFollowing(LoopState state, DateTimeOffset pending)
    {
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

    /// <summary>
    /// GH-5320. Start invoking the due occurrence in the background, or skip it when the previous
    /// invocation has not finished. Never awaited here, so the loop keeps its cadence (and keeps
    /// counting skips) however long the handler takes.
    /// </summary>
    private void runOrSkip(LoopState state, DateTimeOffset occurrence, CancellationToken cancellation)
    {
        if (state.Running is { IsCompleted: false })
        {
            state.SkippedInARow++;
            Interlocked.Increment(ref OccurrencesSkipped);
            _skippedCounter?.Add(1, new KeyValuePair<string, object?>(MetricsConstants.ScheduleNameKey, Message.Name));

            _logger.LogDebug(
                "Skipping the occurrence of recurring message '{Name}' for {Occurrence}; the previous occurrence is still running",
                Message.Name, occurrence);
            return;
        }

        if (state.SkippedInARow > 0)
        {
            _logger.LogInformation(
                "Recurring message '{Name}' skipped {Count} occurrence(s) while the previous one was running, and resumes with {Occurrence}",
                Message.Name, state.SkippedInARow, occurrence);
            state.SkippedInARow = 0;
        }

        Interlocked.Increment(ref OccurrencesPublished);

        // Same GH-4647 reason as the loop itself: the invocation must not inherit an ambient activity
        using (ExecutionContext.SuppressFlow())
        {
            state.Running = Task.Run(() => invokeAsync(occurrence, cancellation), CancellationToken.None);
        }
    }

    private async Task invokeAsync(DateTimeOffset occurrence, CancellationToken cancellation)
    {
        try
        {
            await _publisher.InvokeOccurrenceAsync(Message, occurrence, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // The agent is stopping
        }
        catch (Exception e)
        {
            // Never retried: the next occurrence runs on schedule, like a missed one
            _logger.LogError(e, "The occurrence of recurring message '{Name}' for {Occurrence} failed",
                Message.Name, occurrence);
        }
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
