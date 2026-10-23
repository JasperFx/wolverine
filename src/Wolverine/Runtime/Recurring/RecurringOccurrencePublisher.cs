using System.Collections.Concurrent;
using System.Diagnostics;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Recurring;

/// <summary>
/// The one place a recurring occurrence becomes envelopes. Shared by the singular
/// <see cref="RecurringMessageAgent" /> (pre-scheduled occurrences and manual triggers), the
/// per-schedule <see cref="RecurringTimerAgent" /> (immediate occurrences, GH-5168) and the
/// control surface's manual trigger of a timer schedule, so every path stamps the same identity:
/// the deterministic deduplication id, the <see cref="RecurringMessage.HeaderKey" /> and
/// <see cref="RecurringMessage.OccurrenceHeaderKey" /> headers, and the GH-4647 per-occurrence
/// activity. That shared identity is what lets a rolling deploy (old nodes pre-scheduling, new
/// nodes on timers) collapse to one handling per occurrence.
/// </summary>
internal class RecurringOccurrencePublisher
{
    private readonly IWolverineRuntime _runtime;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, bool> _warnedNoRoutes = new();

    public RecurringOccurrencePublisher(IWolverineRuntime runtime, ILogger logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    /// <summary>
    /// A cron occurrence. <paramref name="scheduledTime" /> is the occurrence itself on the
    /// scheduled-message path, and null on the timer path, which publishes the moment the
    /// occurrence comes due.
    /// </summary>
    public Task<Envelope[]> PublishOccurrenceAsync(RecurringMessage message, DateTimeOffset occurrence,
        DateTimeOffset? scheduledTime)
    {
        // The deterministic occurrence id — identical across nodes, restarts, time zones and
        // execution paths — that lets the GH-4180 dedupe collapse a double-publish at consumption.
        return PublishAsync(message, occurrence, message.DeduplicationIdFor(occurrence), scheduledTime);
    }

    /// <summary>
    /// A manual occurrence: published for IMMEDIATE delivery (no <c>ScheduledTime</c> — "run now"
    /// means now) and carrying its own deduplication id, so it is never collapsed into the
    /// scheduled firing it may coincide with.
    /// </summary>
    public Task<Envelope[]> PublishManualOccurrenceAsync(RecurringMessage message, DateTimeOffset requestedAt)
    {
        return PublishAsync(message, requestedAt, message.ManualDeduplicationIdFor(requestedAt), null);
    }

    public async Task<Envelope[]> PublishAsync(RecurringMessage message, DateTimeOffset occurrence,
        string deduplicationId, DateTimeOffset? scheduledTime)
    {
        var body = message.Creator(occurrence) ?? throw new InvalidOperationException(
            $"The creator for recurring message '{message.Name}' returned null");

        _logger.LogDebug(
            scheduledTime == null
                ? "Publishing an occurrence of recurring message '{Name}' for {Occurrence}"
                : "Scheduling occurrence of recurring message '{Name}' for {Occurrence}",
            message.Name, occurrence);

        var options = new DeliveryOptions
        {
            ScheduledTime = scheduledTime,
            DeduplicationId = deduplicationId
        };
        options.Headers[RecurringMessage.HeaderKey] = message.Name;

        // GH-4445. ScheduledTime is cleared by the scheduled machinery at fire time, so without this
        // the handler cannot tell which firing it is serving except by string-parsing the dedup id,
        // which exists for deduplication and not for attribution. Normalized to UTC and written
        // round-trippable ("O") so it parses back to the same instant everywhere.
        var occurrenceHeader = occurrence.ToUniversalTime().ToString("O");
        options.Headers[RecurringMessage.OccurrenceHeaderKey] = occurrenceHeader;

        // GH-4647. Each occurrence is published inside its own short-lived activity, so the
        // envelope's ParentId (stamped from Activity.Current below) and the bus's correlation id
        // (its root id, taken in the MessageBus constructor) point at a bounded root of this
        // occurrence's own -- never at whatever was ambient on the loop. Started before the bus is
        // built, on purpose, for that second reason. Carries the same schedule identity the
        // handler's execution span reads off the headers, so the two join up in a trace.
        using var activity = WolverineTracing.ActivitySource.StartActivity(WolverineTracing.RecurringOccurrence);
        activity?.SetTag(WolverineTracing.ScheduleName, message.Name);
        activity?.SetTag(WolverineTracing.ScheduleOccurrence, occurrenceHeader);

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
            // loops keep retrying.
            if (_warnedNoRoutes.TryAdd(message.Name, true))
            {
                _logger.LogWarning(
                    "The occurrence of recurring message '{Name}' ({MessageType}) has no subscribers or handlers — nothing was scheduled",
                    message.Name, message.MessageType.FullNameInCode());
            }

            _runtime.MessageTracking.NoRoutesFor(new Envelope(body));
            return outgoing;
        }

        _warnedNoRoutes.TryRemove(message.Name, out _);

        foreach (var envelope in outgoing)
        {
            bus.TrackEnvelopeCorrelation(envelope, Activity.Current);
        }

        await bus.PersistOrSendAsync(outgoing);
        return outgoing;
    }
}
