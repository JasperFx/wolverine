using System;
using System.Diagnostics;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;

namespace Wolverine.Tracking;

public class EnvelopeRecord
{
    public EnvelopeRecord(EventType eventType, Envelope envelope, long sessionTime, Exception? exception)
    {
        Envelope = envelope;
        SessionTime = sessionTime;
        Exception = exception;
        EventType = eventType;
        AttemptNumber = envelope.Attempts;

        var activity = Activity.Current;
        if (activity != null)
        {
            RootId = activity.RootId;
            ParentId = activity.ParentId;
            ActivityId = activity.Id;
        }
    }

    public object? Message => Envelope.Message;

    /// <summary>
    ///     If available, the open telemetry activity id when
    /// </summary>
    public string? ActivityId { get; init; }

    public string? ParentId { get; init; }
    public string? RootId { get; init; }

    public Envelope Envelope { get; }

    /// <summary>
    ///     A timestamp of the milliseconds since the tracked session was started before this event
    /// </summary>
    public long SessionTime { get; }

    public Exception? Exception { get; }
    public EventType EventType { get; }

    public int AttemptNumber { get; }

    public bool IsComplete { get; internal set; }
    public string? ServiceName { get; set; }
    public int UniqueNodeId { get; set; }

    public override string ToString()
    {
        var prefix = $"{ServiceName} ({UniqueNodeId}) @{SessionTime}ms: ";
        var message = $"{Message?.GetType().FullNameInCode()} ({Envelope.Id})";

        switch (EventType)
        {
            case EventType.Sent:
                return $"{prefix}Sent {message} to {Envelope.Destination}";

            case EventType.Received:
                return $"{prefix}Received {message} at {Envelope.Destination}";

            case EventType.ExecutionStarted:
                return $"{prefix}Started execution of {message}";

            case EventType.ExecutionFinished:
                return $"{prefix}Finished execution of {message}";

            case EventType.MessageFailed:
                return $"{prefix}{message} was marked as failed!";

            case EventType.MessageSucceeded:
                return $"{prefix}{message} was marked as successful.";

            case EventType.NoHandlers:
                return $"{prefix}{message} had no known handlers and was discarded";

            case EventType.NoRoutes:
                return $"{prefix}Attempted to publish {message}, but there were no subscribers";

            case EventType.MovedToErrorQueue:
                return $"{prefix}{message} was moved to the dead letter queue";
        }

        var icon = IsComplete ? "+" : "-";
        return
            $"{icon} Service: {ServiceName}, Id: {Envelope.Id}, {nameof(SessionTime)}: {SessionTime}, {nameof(EventType)}: {EventType}, MessageType: {Envelope.MessageType} at node #{UniqueNodeId}";
    }
}