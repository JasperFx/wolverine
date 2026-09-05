using Microsoft.Extensions.Logging;
using Wolverine.Runtime.Agents;

namespace Wolverine.Runtime.Recurring;

/// <summary>
/// The single-per-cluster agent behind <c>opts.Schedules</c>. Its whole job is a guarantee: the
/// NEXT occurrence of every registered <see cref="RecurringMessage" /> is pre-scheduled through
/// Wolverine's existing scheduled-message machinery — <see cref="IMessageBus.PublishAsync" /> with
/// a <see cref="DeliveryOptions.ScheduledTime" /> — and nothing deeper. Delivery, durability,
/// replay-after-crash and management all belong to the machinery that already ships.
///
/// <para>
/// Failure semantics, all deliberate:
/// missed occurrences are SKIPPED, never back-filled — the one pre-scheduled envelope still fires
/// with the agent down, and only occurrences after it in an agent-less window are lost, which is
/// the reason for this shape over a self-perpetuating message (whose chain dies forever on its
/// first discarded envelope). A restart or failover loses only in-memory state; re-publishing the
/// same occurrence produces the same deterministic deduplication id, and the consumption-side
/// dedupe collapses it. A failed publish logs and retries on the next tick — it never kills the
/// loop.
/// </para>
/// </summary>
internal class RecurringMessageAgent : SingularAgent
{
    public const string SchemeName = "wolverine-recurring";

    private readonly IWolverineRuntime _runtime;
    private readonly ILogger<RecurringMessageAgent> _logger;
    private readonly Dictionary<string, DateTimeOffset> _lastPublished = new();

    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private DateTimeOffset _lastTick;

    /// <summary>Total occurrences this agent instance has published. A test seam only.</summary>
    internal int OccurrencesPublished;

    public RecurringMessageAgent(IWolverineRuntime runtime, ILogger<RecurringMessageAgent> logger)
        : base(SchemeName)
    {
        _runtime = runtime;
        _logger = logger;
    }

    /// <summary>
    /// Settable purely as a test seam, matching the house pattern on
    /// <see cref="EventSubscriptionAgent" /> — production code never assigns it.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// The longest the loop sleeps between looks at the schedule table, even when every next
    /// occurrence is far away — a bound on how stale the agent's picture can get, and internal so
    /// tests can tighten it.
    /// </summary>
    internal TimeSpan MaximumTickInterval { get; set; } = TimeSpan.FromSeconds(30);

    public string Description =>
        $"Recurring message scheduler ({_runtime.Options.Schedules.Count} schedule(s))";

    protected override Task startAsync(CancellationToken cancellationToken)
    {
        _cancellation = new CancellationTokenSource();
        _lastTick = TimeProvider.GetUtcNow();
        _loop = Task.Run(() => runAsync(_cancellation.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    protected override async Task stopAsync(CancellationToken cancellationToken)
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
                // The loop is only ever between a publish and a delay; abandoning it on a slow
                // stop is safe because the next start rebuilds all state from the registrations.
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cancellation?.Dispose();
        _cancellation = null;
        _loop = null;
    }

    private async Task runAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            TimeSpan delay;

            try
            {
                delay = await TickAsync(cancellation);
            }
            catch (Exception e)
            {
                // A tick that fails wholesale (not per-schedule — those are caught inside) should
                // never kill the loop; the schedule table is static and the next tick retries.
                _logger.LogError(e, "Error in the recurring message scheduling loop");
                delay = MaximumTickInterval;
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

    /// <summary>
    /// One pass over every registered schedule: publish any next occurrence that is not already in
    /// flight, and return how long to sleep before looking again. Internal (and factored out of
    /// the loop) purely so tests can drive ticks synchronously against a controlled clock.
    /// </summary>
    internal async Task<TimeSpan> TickAsync(CancellationToken cancellation)
    {
        var now = TimeProvider.GetUtcNow();
        _lastTick = now;
        DateTimeOffset? soonest = null;

        foreach (var message in _runtime.Options.Schedules)
        {
            if (cancellation.IsCancellationRequested) break;

            try
            {
                var next = message.Schedule.NextOccurrence(now);
                if (next == null) continue; // no further occurrences — a fixed-date schedule ran out

                if (soonest == null || next < soonest) soonest = next;

                // Only the NEXT occurrence is ever in flight. While it is still in the future the
                // computation keeps answering the same instant and this stays a no-op; once it
                // passes, the following occurrence computes and publishes. Restart/failover wipes
                // this dictionary harmlessly — the re-publish carries the same dedup id.
                if (_lastPublished.TryGetValue(message.Name, out var last) && last == next) continue;

                await publishOccurrenceAsync(message, next.Value);
                _lastPublished[message.Name] = next.Value;
                Interlocked.Increment(ref OccurrencesPublished);
            }
            catch (Exception e)
            {
                _logger.LogError(e,
                    "Failed to schedule the next occurrence of recurring message '{Name}'; will retry on the next pass",
                    message.Name);
            }
        }

        if (soonest == null) return MaximumTickInterval;

        var untilNext = soonest.Value - now;
        if (untilNext > MaximumTickInterval) return MaximumTickInterval;
        return untilNext < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : untilNext;
    }

    private Task publishOccurrenceAsync(RecurringMessage message, DateTimeOffset occurrence)
    {
        var body = message.Creator(occurrence) ?? throw new InvalidOperationException(
            $"The creator for recurring message '{message.Name}' returned null");

        _logger.LogDebug("Scheduling occurrence of recurring message '{Name}' for {Occurrence}",
            message.Name, occurrence);

        var options = new DeliveryOptions
        {
            ScheduledTime = occurrence,

            // The deterministic occurrence id — identical across nodes, restarts and time zones —
            // that lets the GH-4180 dedupe collapse a failover double-publish at consumption.
            DeduplicationId = message.DeduplicationIdFor(occurrence)
        };
        options.Headers[RecurringMessage.HeaderKey] = message.Name;

        IMessageBus bus = new MessageBus(_runtime);
        return bus.PublishAsync(body, options).AsTask();
    }
}
