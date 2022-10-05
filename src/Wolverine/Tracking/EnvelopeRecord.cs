using System;
using System.Diagnostics;

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

    /// <summary>
    /// If available, the open telemetry activity id when
    /// </summary>
    public string? ActivityId { get; init; }
    public string? ParentId { get; init; }
    public string? RootId { get; init; }

    public Envelope Envelope { get; }
    public long SessionTime { get; }
    public Exception? Exception { get; }
    public EventType EventType { get; }

    public int AttemptNumber { get; }

    public bool IsComplete { get; internal set; }
    public string? ServiceName { get; set; }
    public int UniqueNodeId { get; set; }

    public override string ToString()
    {
        var icon = IsComplete ? "+" : "-";
        return
            $"{icon} Service: {ServiceName}, Id: {Envelope.Id}, {nameof(SessionTime)}: {SessionTime}, {nameof(EventType)}: {EventType}, MessageType: {Envelope.MessageType} at node #{UniqueNodeId} --> {IsComplete}";
    }
}
