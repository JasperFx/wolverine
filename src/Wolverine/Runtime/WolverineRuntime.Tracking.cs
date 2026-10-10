using System.Diagnostics;
using System.Diagnostics.Metrics;
using ImTools;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Runtime.Metrics;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace Wolverine.Runtime;

public sealed partial class WolverineRuntime : IMessageTracker
{
    public const int SentEventId = 100;
    public const int ReceivedEventId = 101;
    public const int NoHandlerEventId = 106;
    public const int NoRoutesEventId = 107;
    public const int MovedToErrorQueueId = 108;
    public const int UndeliverableEventId = 108;
    public const int RescheduledEventId = 109;

    private static readonly Action<ILogger, Envelope, Exception?> _movedToErrorQueue;
    private static readonly Action<ILogger, Envelope, Exception?> _rescheduled;
    private static readonly Action<ILogger, string?, string, Guid, string, Exception?> _noHandler;
    private static readonly Action<ILogger, Envelope, Exception?> _noRoutes;
    private static readonly Action<ILogger, string, string, Guid, string, string, Exception?> _received;
    private static readonly Action<ILogger, string, string, Guid, string, Exception?> _sent;
    private static readonly Action<ILogger, Envelope, Exception?> _undeliverable;
    private readonly Counter<int> _deadLetterQueueCounter;
    private readonly Histogram<double> _effectiveTime;
    private readonly Histogram<double> _executionCounter;
    private readonly Counter<int> _failureCounter;
    private readonly Counter<int> _receivedCounter;
    private readonly Counter<int> _sentCounter;
    private readonly Counter<int> _successCounter;

    static WolverineRuntime()
    {
        _sent = LoggerMessage.Define<string, string, Guid, string>(LogLevel.Debug, SentEventId,
            "{CorrelationId}: Enqueued for sending {Name}#{Id} to {Destination}");

        _received = LoggerMessage.Define<string, string, Guid, string, string>(LogLevel.Debug, ReceivedEventId,
            "{CorrelationId}: Received {Name}#{Id} at {Destination} from {ReplyUri}");

        _noHandler = LoggerMessage.Define<string?, string, Guid, string>(LogLevel.Information, NoHandlerEventId,
            "{CorrelationId}: No known handler for {Name}#{Id} from {ReplyUri}");

        _noRoutes = LoggerMessage.Define<Envelope>(LogLevel.Information, NoRoutesEventId,
            "No routes can be determined for {envelope}");

        _rescheduled = LoggerMessage.Define<Envelope>(LogLevel.Error, RescheduledEventId,
            "Envelope {envelope} was rescheduled to queue");

        _movedToErrorQueue = LoggerMessage.Define<Envelope>(LogLevel.Error, MovedToErrorQueueId,
            "Envelope {envelope} was moved to the error queue");

        _undeliverable = LoggerMessage.Define<Envelope>(LogLevel.Information, UndeliverableEventId,
            "Discarding {envelope}");
    }

    internal TrackedSession? ActiveSession { get; set; }

    /// <summary>
    ///     GH-4931. Record one event-store session's committed streams against the active tracked session, if
    ///     there is one, attributed to the message <paramref name="context" /> was handling.
    /// </summary>
    internal void RecordAppendedEvents(MessageContext context, IReadOnlyList<StreamAction> streams)
    {
        if (streams.Count == 0) return;
        ActiveSession?.RecordAppendedEvents(new AppendedEvents(context.Envelope, _serviceName, streams));
    }

    /// <summary>
    /// Build the metric tag set for an envelope: the standard tags (message.type + message.destination +
    /// tenant.id + any custom SetMetricsTag values) plus the <c>source</c> service-name tag. The
    /// <c>source</c> tag is added to *every* instrument (GH-3221) so a shared metrics backend that scrapes
    /// many services can slice each series per service.
    /// </summary>
    private TagList metricTags(Envelope envelope)
    {
        var tags = envelope.ToMetricsHeaders();
        tags.Add(MetricsConstants.SourceKey, _serviceName);
        return tags;
    }

    public void Sent(Envelope envelope)
    {
        _sentCounter.Add(1, metricTags(envelope));

        if (Options.Metrics.Mode != WolverineMetricsMode.SystemDiagnosticsMeter
            && envelope.MessageType.IsNotEmpty()
            && !IsSystemEndpoint(envelope.Destination))
        {
            var accumulator = _accumulator.Value.FindAccumulator(envelope.MessageType!, envelope.Destination!);
            accumulator.EntryPoint.Post(new RecordSent(envelope.TenantId!, _serviceName));
        }

        ActiveSession?.MaybeRecord(MessageEventType.Sent, envelope, _serviceName, _uniqueNodeId);
        _sent(Logger, envelope.CorrelationId!, envelope.GetMessageTypeName(), envelope.Id,
            envelope.Destination?.ToString() ?? string.Empty,
            null);

        fireWireTapSuccess(envelope);
    }

    public void Received(Envelope envelope)
    {
        var isExternal = IsExternalDestination(envelope.Destination);

        if (isExternal)
        {
            _receivedCounter.Add(1, metricTags(envelope));
        }

        if (isExternal && Options.Metrics.Mode != WolverineMetricsMode.SystemDiagnosticsMeter
            && envelope.MessageType.IsNotEmpty()
            && !IsSystemEndpoint(envelope.Destination))
        {
            var accumulator = _accumulator.Value.FindAccumulator(envelope.MessageType!, envelope.Destination!);
            accumulator.EntryPoint.Post(new RecordReceived(envelope.TenantId!, _serviceName));
        }

        ActiveSession?.Record(MessageEventType.Received, envelope, _serviceName, _uniqueNodeId);
        _received(Logger, envelope.CorrelationId!, envelope.GetMessageTypeName(), envelope.Id,
            envelope.Destination?.ToString() ?? string.Empty,
            envelope.ReplyUri?.ToString() ?? string.Empty, null);
    }

    public void ExecutionStarted(Envelope envelope)
    {
        envelope.StartTiming();
        ActiveSession?.Record(MessageEventType.ExecutionStarted, envelope, _serviceName, _uniqueNodeId);
    }

    public void ExecutionFinished(Envelope envelope)
    {
        var time = envelope.StopTiming();
        if (time >= 0)
        {
            _executionCounter.Record(time, metricTags(envelope));
        }

        ActiveSession?.Record(MessageEventType.ExecutionFinished, envelope, _serviceName, _uniqueNodeId);
    }

    public void ExecutionFinished(Envelope envelope, Exception exception)
    {
        ExecutionFinished(envelope);
        var tags = metricTags(envelope);
        tags.Add(MetricsConstants.ExceptionType, exception.GetType().Name);
        _failureCounter.Add(1, tags);
    }

    public void MessageSucceeded(Envelope envelope)
    {
        var tags = metricTags(envelope);
        _successCounter.Add(1, tags);

        // An unset SentAt makes now - SentAt a ~56-year garbage figure; skip the effective-time
        // recording rather than publish it (CritterWatch#880's arithmetic half)
        if (envelope.SentAt == default)
        {
            ActiveSession?.Record(MessageEventType.MessageSucceeded, envelope, _serviceName, _uniqueNodeId);
            fireWireTapSuccess(envelope);
            return;
        }

        var time = DateTimeOffset.UtcNow.Subtract(envelope.SentAt.ToUniversalTime()).TotalMilliseconds;
        _effectiveTime.Record(time, tags);

        if (Options.Metrics.Mode != WolverineMetricsMode.SystemDiagnosticsMeter
            && envelope.MessageType.IsNotEmpty()
            && !IsSystemEndpoint(envelope.Destination))
        {
            var accumulator = _accumulator.Value.FindAccumulator(envelope.MessageType!, envelope.Destination!);
            accumulator.EntryPoint.Post(new RecordEffectiveTime(time, envelope.TenantId!));
        }

        ActiveSession?.Record(MessageEventType.MessageSucceeded, envelope, _serviceName, _uniqueNodeId);

        fireWireTapSuccess(envelope);
    }

    public void MessageFailed(Envelope envelope, Exception ex)
    {
        var tags = metricTags(envelope);
        _deadLetterQueueCounter.Add(1, tags);

        // Same unset-SentAt guard as MessageSucceeded (CritterWatch#880's arithmetic half)
        if (envelope.SentAt == default)
        {
            ActiveSession?.Record(MessageEventType.MessageFailed, envelope, _serviceName, _uniqueNodeId, ex);
            fireWireTapFailure(envelope, ex);
            return;
        }

        var time = DateTimeOffset.UtcNow.Subtract(envelope.SentAt.ToUniversalTime()).TotalMilliseconds;
        _effectiveTime.Record(time, tags);

        if (Options.Metrics.Mode != WolverineMetricsMode.SystemDiagnosticsMeter
            && envelope.MessageType.IsNotEmpty()
            && !IsSystemEndpoint(envelope.Destination))
        {
            var accumulator = _accumulator.Value.FindAccumulator(envelope.MessageType!, envelope.Destination!);
            accumulator.EntryPoint.Post(new RecordEffectiveTime(time, envelope.TenantId!));
        }

        // GH-4136: record MessageFailed, not Sent. Sent is NOT terminal -- an EnvelopeHistory only
        // completes a Sent record once a matching Received arrives -- so a failed message never
        // reached a terminal state in a tracked session and the session could only ever end by timing
        // out. It went unnoticed because the dead-letter path emits its own terminal
        // MovedToErrorQueue record, covering the one path anybody tested.
        ActiveSession?.Record(MessageEventType.MessageFailed, envelope, _serviceName, _uniqueNodeId, ex);

        fireWireTapFailure(envelope, ex);
    }

    public void NoHandlerFor(Envelope envelope)
    {
        ActiveSession?.Record(MessageEventType.NoHandlers, envelope, _serviceName, _uniqueNodeId);
        _noHandler(Logger, envelope.CorrelationId, envelope.GetMessageTypeName(), envelope.Id,
            envelope.ReplyUri?.ToString() ?? string.Empty,
            null);
    }

    public void NoRoutesFor(Envelope envelope)
    {
        ActiveSession?.Record(MessageEventType.NoRoutes, envelope, _serviceName, _uniqueNodeId);
        _noRoutes(Logger, envelope, null);
    }

    /// <summary>
    /// GH-4136: this is the <b>only</b> tracking event the dead-letter path reports, and it carries the
    /// exception. MoveToErrorQueue used to call MessageFailed as well, which was harmless while
    /// MessageFailed recorded the non-terminal Sent -- MovedToErrorQueue came last and swept every
    /// prior record complete. Once MessageFailed became terminal, two terminal records on one path
    /// broke both orderings: MessageFailed first completes the envelope and the session can resume
    /// before MovedToErrorQueue lands (TransportCompliance's "No ending activity detected"), while
    /// MovedToErrorQueue first stops sweeping the trailing Sent that the durable transports' own DLQ
    /// move produces, so the session never completes at all. One terminal event, reported last, is
    /// the only arrangement that satisfies both. MessageFailed's dead-letter counter, effective-time
    /// recording and failure wire tap are folded in here so no metric is lost.
    /// </summary>
    public void MovedToErrorQueue(Envelope envelope, Exception ex)
    {
        var tags = metricTags(envelope);
        _deadLetterQueueCounter.Add(1, tags);

        // Same unset-SentAt guard as MessageSucceeded (CritterWatch#880's arithmetic half)
        if (envelope.SentAt != default)
        {
            var time = DateTimeOffset.UtcNow.Subtract(envelope.SentAt.ToUniversalTime()).TotalMilliseconds;
            _effectiveTime.Record(time, tags);
        }

        ActiveSession?.Record(MessageEventType.MovedToErrorQueue, envelope, _serviceName, _uniqueNodeId, ex);
        fireWireTapFailure(envelope, ex);
        _movedToErrorQueue(Logger, envelope, ex);

        if (Options.Metrics.Mode != WolverineMetricsMode.SystemDiagnosticsMeter
            && envelope.MessageType.IsNotEmpty()
            && !IsSystemEndpoint(envelope.Destination))
        {
            var accumulator = _accumulator.Value.FindAccumulator(envelope.MessageType!, envelope.Destination!);
            accumulator.EntryPoint.Post(new RecordDeadLetter(ex.GetType().FullNameInCode(), envelope.TenantId!));
        }
    }

    public void DiscardedEnvelope(Envelope envelope)
    {
        _undeliverable(Logger, envelope, null);
        ActiveSession?.Record(MessageEventType.Discarded, envelope, _serviceName, _uniqueNodeId);
    }

    public void Requeued(Envelope envelope)
    {
        Logger.LogInformation("Requeue for message {Id} of message type {MessageType}", envelope.Id, envelope.MessageType);
        ActiveSession?.Record(MessageEventType.Requeued, envelope, _serviceName, _uniqueNodeId);
        _rescheduled(Logger, envelope, null);
    }

    public void LogException(Exception ex, object? correlationId = null,
        string message = "Exception detected:")
    {
        ActiveSession?.LogException(ex, _serviceName);
        Logger.LogError(ex, message);
    }

    public void LogStatus(string message)
    {
        ActiveSession?.LogStatus(message);
    }

    private void fireWireTapSuccess(Envelope envelope)
    {
        if (envelope.WireTap == null) return;
        try
        {
            _ = envelope.WireTap.RecordSuccessAsync(envelope);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Wire tap failed for envelope {EnvelopeId}", envelope.Id);
        }
    }

    private void fireWireTapFailure(Envelope envelope, Exception exception)
    {
        if (envelope.WireTap == null) return;
        try
        {
            _ = envelope.WireTap.RecordFailureAsync(envelope, exception);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Wire tap failed for envelope {EnvelopeId}", envelope.Id);
        }
    }

    // GH-4324: both classifications below are pure functions of a destination Uri drawn from a
    // small bounded set (endpoints plus per-node reply queues), yet ran per message — the system
    // check with a full ToString() substring scan, the external check with two case-insensitive
    // scheme compares, 2-4x per message between them. One lock-free trie probe answers both; a
    // lost racing update just recomputes the same value next time.
    // GH-4665: instance-scoped, because the System half now asks the endpoint what role it plays and
    // that answer belongs to one runtime's configuration, not to the process.
    private ImHashMap<Uri, DestinationClassification> _destinationClassifications =
        ImHashMap<Uri, DestinationClassification>.Empty;

    [Flags]
    private enum DestinationClassification
    {
        None = 0,
        System = 1,
        External = 2
    }

    private DestinationClassification classifyDestination(Uri destination)
    {
        if (_destinationClassifications.TryFind(destination, out var classification))
        {
            return classification;
        }

        var isLocal = destination.Scheme.EqualsIgnoreCase(TransportConstants.Local);

        if (isSystemTraffic(destination, isLocal))
        {
            classification |= DestinationClassification.System;
        }

        if (!isLocal && !destination.Scheme.EqualsIgnoreCase("stub"))
        {
            classification |= DestinationClassification.External;
        }

        _destinationClassifications = _destinationClassifications.AddOrUpdate(destination, classification);
        return classification;
    }

    /// <summary>
    /// GH-4665. The question this answers is "is this Wolverine's own plumbing", and
    /// <see cref="EndpointRole" /> is what actually records that — it is already the discriminator
    /// <c>MessageTrackingFor</c> and <c>ExecutorFactory.trackerFor</c> use. This used to test the
    /// <c>local</c> scheme instead, which swept in every user queue: <see cref="LocalTransport" /> marks
    /// only <c>scheduled</c>, <c>durable</c> and <c>agents</c> as <see cref="EndpointRole.System" />, so a
    /// handler on an ordinary local queue had its dead letters silently dropped from the per-type and
    /// per-tenant metrics while its executions and failures were counted.
    /// </summary>
    private bool isSystemTraffic(Uri destination, bool isLocal)
    {
        // Per-node broker reply queues: named rather than registered as role-System everywhere, and the
        // token survives an Azure Service Bus application prefix, so this stays a Contains().
        if (destination.ToString().Contains("wolverine.response", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The local reply queue is the same plumbing as those, but LocalTransport leaves it Application.
        if (isLocal && destination == TransportConstants.RepliesUri)
        {
            return true;
        }

        // One linear scan per distinct destination Uri, then never again -- the caller caches. Same shape
        // as EndpointCollection.IsSingleNodeListener. An unregistered destination is not system traffic:
        // counting a stray is far better than silently dropping a user's dead letters, which is the bug.
        return Endpoints.EndpointFor(destination)?.Role == EndpointRole.System;
    }

    /// <summary>
    /// Returns true if the destination URI belongs to Wolverine's own plumbing — a
    /// <c>wolverine.response</c> reply queue, or an endpoint whose <see cref="EndpointRole" /> is
    /// <see cref="EndpointRole.System" /> — which should not be tracked in the CritterWatch
    /// accumulation pipeline. A user's local queue is NOT system traffic.
    /// </summary>
    internal bool IsSystemEndpoint(Uri? destination)
    {
        return destination != null &&
               (classifyDestination(destination) & DestinationClassification.System) != 0;
    }

    /// <summary>
    /// Returns true if the destination URI points at an external transport endpoint —
    /// i.e. anything other than a local queue or a stub — the gate for the external
    /// send/receive counters.
    /// </summary>
    internal bool IsExternalDestination(Uri? destination)
    {
        return destination != null &&
               (classifyDestination(destination) & DestinationClassification.External) != 0;
    }
}