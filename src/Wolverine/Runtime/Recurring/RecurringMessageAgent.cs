using System.Diagnostics;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Agents;

namespace Wolverine.Runtime.Recurring;

/// <summary>
/// The single-per-cluster agent behind <c>opts.Schedules</c>. Its whole job is a guarantee: the
/// NEXT occurrence of every registered <see cref="RecurringMessage" /> is pre-scheduled through
/// Wolverine's existing scheduled-message machinery — a routed publish with a
/// <see cref="DeliveryOptions.ScheduledTime" /> — and nothing deeper. Delivery, durability,
/// replay-after-crash and management all belong to the machinery that already ships.
///
/// <para>
/// Where the message store carries the recurring tracking extension
/// (<see cref="IRecurringMessageStore" />), the guarantee is record-and-verify rather than
/// fire-and-forget: each publish records the pre-scheduled envelope id(s) on the schedule's
/// tracking row, the loop occasionally confirms those envelopes still sit
/// <see cref="EnvelopeStatus.Scheduled" /> in the inbox (re-publishing when something cancelled
/// them out from under the schedule), and a failover successor ADOPTS a predecessor's verified
/// pending occurrence off the row instead of blindly re-publishing it. The row also carries the
/// pause flag the loop honours every tick.
/// </para>
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
    private readonly Dictionary<string, DateTimeOffset> _lastVerified = new();
    private readonly HashSet<string> _warnedNoRoutes = new();

    // Only the channel for pause/resume on a store WITHOUT the recurring tracking extension —
    // with a real store the durable row is the single channel, deliberately (see
    // RecurringScheduleControl). Locked because the control service writes from caller threads
    // while the loop reads.
    private readonly HashSet<string> _locallyPaused = new();
    private readonly object _pauseLock = new();

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

    /// <summary>
    /// How often the loop re-confirms that a tracked pre-scheduled envelope still sits Scheduled
    /// in the inbox. "Occasionally" on purpose: the check is a cheap indexed count, but the thing
    /// it catches — an occurrence cancelled or lost out from under its schedule — is rare, and a
    /// re-publish carries the same deduplication id either way. Internal so tests can tighten it.
    /// </summary>
    internal TimeSpan VerificationInterval { get; set; } = TimeSpan.FromMinutes(5);

    public string Description =>
        $"Recurring message scheduler ({_runtime.Options.Schedules.Count} schedule(s))";

    /// <summary>
    /// Pause a schedule in THIS instance's memory — the fallback channel when the message store
    /// has no recurring tracking extension. Does not (cannot) cancel an already-pre-scheduled
    /// occurrence, so the pending one still fires; only future publishes stop. The in-memory
    /// last-published bookkeeping is deliberately kept, so a resume before the pending occurrence
    /// does not double-publish it.
    /// </summary>
    internal void MarkPaused(string name)
    {
        lock (_pauseLock)
        {
            _locallyPaused.Add(name);
        }
    }

    /// <summary>The in-memory resume half of <see cref="MarkPaused" />.</summary>
    internal void MarkResumed(string name)
    {
        lock (_pauseLock)
        {
            _locallyPaused.Remove(name);
        }
    }

    private bool isLocallyPaused(string name)
    {
        lock (_pauseLock)
        {
            return _locallyPaused.Contains(name);
        }
    }

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
        // The agent machinery starts BEFORE the messaging transports, and a RoutingFor answer
        // computed in that window can come back empty and be cached for the life of the host —
        // a first-tick publish would then silently go nowhere, forever. Wait for the runtime to
        // be fully started (transports up, routing cache pre-populated with real routes).
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
    /// One pass over every registered schedule: honour pause flags, verify or adopt tracked
    /// occurrences, publish any next occurrence that is not already in flight, and return how
    /// long to sleep before looking again. Internal (and factored out of the loop) purely so
    /// tests can drive ticks synchronously against a controlled clock.
    /// </summary>
    internal async Task<TimeSpan> TickAsync(CancellationToken cancellation)
    {
        var now = TimeProvider.GetUtcNow();
        _lastTick = now;
        DateTimeOffset? soonest = null;

        var store = _runtime.Storage.RecurringMessages;
        var rows = await tryLoadTrackingRowsAsync(store, cancellation);

        foreach (var message in _runtime.Options.Schedules)
        {
            if (cancellation.IsCancellationRequested) break;

            try
            {
                var row = rows?.GetValueOrDefault(message.Name);

                if (row is { Paused: true } || isLocallyPaused(message.Name))
                {
                    await honourPauseAsync(store, message.Name, row, now, cancellation);
                    continue;
                }

                var next = message.Schedule.NextOccurrence(now);
                if (next == null) continue; // no further occurrences — a fixed-date schedule ran out

                if (soonest == null || next < soonest) soonest = next;

                // Only the NEXT occurrence is ever in flight. While it is still in the future the
                // computation keeps answering the same instant; the tracked row lets us verify it
                // is really pending (and re-publish when it is not), and lets a failover successor
                // adopt a predecessor's publish instead of duplicating it.
                if (_lastPublished.TryGetValue(message.Name, out var last) && last == next)
                {
                    if (!await isDueForReverificationAsync(store, message, row, next.Value, now, cancellation))
                    {
                        continue;
                    }

                    // The tracked envelope is gone — something cancelled or lost it. Fall through
                    // and re-publish this same occurrence (same deduplication id, so an envelope
                    // that actually fired in the gap still collapses to one handling).
                    _lastPublished.Remove(message.Name);
                    _logger.LogWarning(
                        "The pre-scheduled occurrence of recurring message '{Name}' at {Occurrence} is no " +
                        "longer in the inbox; re-publishing it", message.Name, next.Value);
                }
                else if (!_lastPublished.ContainsKey(message.Name) &&
                         await canAdoptTrackedOccurrenceAsync(store, row, next.Value, now, cancellation))
                {
                    // Failover/restart: a previous agent instance already pre-scheduled exactly
                    // this occurrence, and the inbox confirms it is still pending. Adopt it.
                    _lastPublished[message.Name] = next.Value;
                    continue;
                }

                var outgoing = await publishOccurrenceAsync(message, next.Value);
                if (outgoing.Length == 0)
                {
                    // No routes is NOT a publish — marking it as one would silently skip the
                    // occurrence. Leave the schedule unmarked so the next tick retries; if a
                    // subscription appears (or routing finishes waking up), the occurrence is
                    // still scheduled on time.
                    continue;
                }

                _lastPublished[message.Name] = next.Value;
                Interlocked.Increment(ref OccurrencesPublished);

                await recordPublishAsync(store, message, next.Value, now, outgoing, cancellation);
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

    private async Task<Dictionary<string, RecurringMessageRecord>?> tryLoadTrackingRowsAsync(
        IRecurringMessageStore store, CancellationToken cancellation)
    {
        if (!store.Enabled) return null;

        try
        {
            var rows = await store.LoadAllAsync(cancellation);
            return rows.ToDictionary(x => x.Name);
        }
        catch (Exception e)
        {
            // Publishing is governed by in-memory state, so the tick can proceed without the
            // rows; what is lost until the store answers again is pause visibility and
            // verification, not delivery.
            _logger.LogError(e, "Unable to load the recurring message tracking rows; continuing without them");
            return null;
        }
    }

    private async Task honourPauseAsync(IRecurringMessageStore store, string name,
        RecurringMessageRecord? row, DateTimeOffset now, CancellationToken cancellation)
    {
        if (!store.Enabled)
        {
            // In-memory pause cannot cancel the pending occurrence, so the last-published
            // bookkeeping is deliberately KEPT — a resume before that occurrence must not
            // publish it a second time onto a store with no deduplication.
            return;
        }

        // Durable pause: the pending envelope was cancelled eagerly by PauseAsync, so forget it.
        _lastPublished.Remove(name);
        _lastVerified.Remove(name);

        if (row is { Paused: true, EnvelopeIds.Length: > 0 })
        {
            // A publish raced the pause (or the pausing node died mid-cancel), leaving a tracked
            // envelope pending on a paused schedule. PauseAsync is idempotent — re-running it
            // finishes the eager cancel.
            await store.PauseAsync(name, row.PausedAt ?? now, cancellation);
        }
    }

    /// <summary>
    /// Returns true when the tracked envelopes for a pending occurrence are due a check AND that
    /// check found at least one of them missing from the inbox — i.e. the occurrence needs
    /// re-publishing. Any other answer (not enabled, not due yet, row does not match, all still
    /// scheduled) is false: leave the occurrence alone.
    /// </summary>
    private async Task<bool> isDueForReverificationAsync(IRecurringMessageStore store,
        RecurringMessage message, RecurringMessageRecord? row, DateTimeOffset next,
        DateTimeOffset now, CancellationToken cancellation)
    {
        if (!store.Enabled) return false;
        if (row == null || row.NextOccurrence != next || row.EnvelopeIds.Length == 0) return false;

        if (_lastVerified.TryGetValue(message.Name, out var verified) &&
            now - verified < VerificationInterval)
        {
            return false;
        }

        var stillScheduled = await store.CountStillScheduledAsync(row.EnvelopeIds, cancellation);
        _lastVerified[message.Name] = now;

        return stillScheduled < row.EnvelopeIds.Length;
    }

    private async Task<bool> canAdoptTrackedOccurrenceAsync(IRecurringMessageStore store,
        RecurringMessageRecord? row, DateTimeOffset next, DateTimeOffset now,
        CancellationToken cancellation)
    {
        if (!store.Enabled) return false;
        if (row == null || row.NextOccurrence != next || row.EnvelopeIds.Length == 0) return false;

        var stillScheduled = await store.CountStillScheduledAsync(row.EnvelopeIds, cancellation);
        if (stillScheduled < row.EnvelopeIds.Length) return false;

        _lastVerified[row.Name] = now;
        return true;
    }

    private async Task<Envelope[]> publishOccurrenceAsync(RecurringMessage message, DateTimeOffset occurrence)
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

        // The routed-then-persisted spelling of IMessageBus.PublishAsync, taken apart only
        // because the public path never surfaces the envelopes (GH-4180's own analysis) and the
        // tracking row needs their ids. Everything else — routing, correlation stamping, the
        // persist-or-send decision — is the same machinery PublishAsync runs.
        var bus = new MessageBus(_runtime);
        var outgoing = _runtime.RoutingFor(body.GetType()).RouteForPublish(body, options);

        if (outgoing.Length == 0)
        {
            // For a registered schedule this is a misconfiguration worth saying out loud — the
            // cron ticks forever and every occurrence goes nowhere. Once per schedule, since the
            // tick loop keeps retrying.
            if (_warnedNoRoutes.Add(message.Name))
            {
                _logger.LogWarning(
                    "The occurrence of recurring message '{Name}' ({MessageType}) has no subscribers or handlers — nothing was scheduled",
                    message.Name, message.MessageType.FullNameInCode());
            }

            _runtime.MessageTracking.NoRoutesFor(new Envelope(body));
            return outgoing;
        }

        _warnedNoRoutes.Remove(message.Name);

        foreach (var envelope in outgoing)
        {
            bus.TrackEnvelopeCorrelation(envelope, Activity.Current);
        }

        await bus.PersistOrSendAsync(outgoing);
        return outgoing;
    }

    private async Task recordPublishAsync(IRecurringMessageStore store, RecurringMessage message,
        DateTimeOffset occurrence, DateTimeOffset now, Envelope[] outgoing, CancellationToken cancellation)
    {
        if (!store.Enabled) return;

        try
        {
            // Only envelopes that actually landed in the durable inbox as Scheduled are
            // verifiable and cancellable; an occurrence riding a broker's native delayed
            // delivery (or an in-memory queue) is deliberately not tracked — verification
            // would keep "finding it missing" and re-publish it forever.
            // Scheduled status alone is not enough: routing stamps it on BUFFERED local queue
            // envelopes too, whose occurrences live in the in-memory scheduler and never reach
            // the inbox — recording those would make verification "find them missing" and
            // re-publish forever. Only a durable sender's scheduled envelope becomes an inbox
            // row (the durable local queue, or the durable-local wrapper a non-native remote
            // scheduled send rides in).
            var scheduledIds = outgoing
                .Where(x => x is { Status: EnvelopeStatus.Scheduled, Sender.IsDurable: true })
                .Select(x => x.Id)
                .ToArray();

            if (scheduledIds.Length == 0)
            {
                _logger.LogDebug(
                    "No occurrence envelope of recurring message '{Name}' landed in the durable inbox " +
                    "(destinations: {Destinations}; statuses: {Statuses}) — the occurrence will fire, but " +
                    "cannot be verified or cancelled through the tracking extension",
                    message.Name,
                    outgoing.Select(x => x.Destination?.ToString() ?? "?").Join(", "),
                    outgoing.Select(x => x.Status.ToString()).Join(", "));
            }

            await store.RecordPublishedAsync(new RecurringMessageRecord
            {
                Name = message.Name,
                CronExpression = message.Schedule.Expression,
                EnvelopeIds = scheduledIds,
                DeduplicationId = message.DeduplicationIdFor(occurrence),
                NextOccurrence = occurrence,
                LastUpdated = now
            }, cancellation);
        }
        catch (Exception e)
        {
            // The publish itself succeeded — a lost record degrades verification and management
            // visibility for this occurrence, never delivery. The next publish re-records.
            _logger.LogError(e,
                "Failed to record the tracking row for recurring message '{Name}'", message.Name);
        }
    }
}
