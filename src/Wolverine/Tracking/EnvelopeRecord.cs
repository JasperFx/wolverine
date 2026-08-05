using System.Diagnostics;
using JasperFx.Core.Reflection;
using Wolverine.Transports;

namespace Wolverine.Tracking;

public class EnvelopeRecord
{
    private static long _sequence;

    public EnvelopeRecord(MessageEventType eventType, Envelope? envelope, long sessionTime, Exception? exception)
    {
        Sequence = Interlocked.Increment(ref _sequence);
        Envelope = envelope;
        SessionTime = sessionTime;
        Exception = exception;
        MessageEventType = eventType;
        AttemptNumber = envelope?.Attempts ?? 0;

        WasScheduled = envelope?.IsScheduledForLater(DateTimeOffset.UtcNow) ?? false;

        var activity = Activity.Current;
        if (activity != null)
        {
            RootId = activity.RootId;
            ParentId = activity.ParentId;
            ActivityId = activity.Id;
        }
    }
    
    public bool WasScheduled { get; set; }

    public object? Message => Envelope?.Message;

    /// <summary>
    ///     If available, the open telemetry activity id when
    /// </summary>
    public string? ActivityId { get; init; }

    public string? ParentId { get; init; }
    public string? RootId { get; init; }

    public Envelope? Envelope { get; private set; }

    /// <summary>
    ///     A timestamp of the milliseconds since the tracked session was started before this event
    /// </summary>
    public long SessionTime { get; }

    /// <summary>
    ///     Globally monotonic ordinal assigned when this record was created. GH-3825: SessionTime is
    ///     only millisecond resolution, so every record from one receive batch lands on the same value
    ///     and sorting by it degenerates to the (unordered) enumeration of the envelope cache. This is
    ///     the tiebreaker that makes AllRecordsInOrder() actually report activity in the order it
    ///     happened.
    /// </summary>
    public long Sequence { get; }

    public Exception? Exception { get; }
    public MessageEventType MessageEventType { get; private set; }

    public int AttemptNumber { get; }

    public bool IsComplete { get; internal set; }
    public string? ServiceName { get; set; }
    public Guid UniqueNodeId { get; set; }

    public override string ToString()
    {
        var prefix = $"{ServiceName} ({UniqueNodeId}) @{SessionTime}ms: ";
        var message = $"{Message?.GetType().FullNameInCode()} ({Envelope!.Id})";

        switch (MessageEventType)
        {
            case MessageEventType.Sent:
                return $"{prefix}Sent {message} to {Envelope.Destination}";
            
            case MessageEventType.Scheduled:
                return
                    $"{prefix}Scheduled {message} to {Envelope.Destination} at {Envelope.ScheduledTime?.ToString("O")}";

            case MessageEventType.Received:
                return $"{prefix}Received {message} at {Envelope.Destination}";

            case MessageEventType.ExecutionStarted:
                return $"{prefix}Started execution of {message}";

            case MessageEventType.ExecutionFinished:
                return $"{prefix}Finished execution of {message}";

            case MessageEventType.MessageFailed:
                return $"{prefix}{message} was marked as failed!";

            case MessageEventType.MessageSucceeded:
                return $"{prefix}{message} was marked as successful.";

            case MessageEventType.NoHandlers:
                return $"{prefix}{message} had no known handlers and was discarded";

            case MessageEventType.NoRoutes:
                return $"{prefix}Attempted to publish {message}, but there were no subscribers";

            case MessageEventType.MovedToErrorQueue:
                return $"{prefix}{message} was moved to the dead letter queue";

            case MessageEventType.AutoFaultPublished:
                return $"{prefix}Auto-published Fault for {message}";
        }

        var icon = IsComplete ? "+" : "-";
        return
            $"{icon} Service: {ServiceName}, Id: {Envelope.Id}, {nameof(SessionTime)}: {SessionTime}, {nameof(MessageEventType)}: {MessageEventType}, MessageType: {Envelope.MessageType} at node #{UniqueNodeId}";
    }

    internal void TryUseInnerFromScheduledEnvelope()
    {
        if (Envelope!.MessageType == TransportConstants.ScheduledEnvelope)
        {
            MessageEventType = MessageEventType.Scheduled;
            Envelope = (Envelope)Envelope.Message!;
        }
    }
}