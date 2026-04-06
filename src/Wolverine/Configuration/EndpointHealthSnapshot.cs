namespace Wolverine.Configuration;

/// <summary>
/// A point-in-time snapshot of health state for a single messaging endpoint.
/// </summary>
public record EndpointHealthSnapshot(
    Uri Uri,
    string EndpointName,
    EndpointDirection Direction,
    string Status,
    int QueueCount,
    DateTimeOffset? LastQueueActivityAt,
    DateTimeOffset? LastMessageSentAt,
    bool SenderLatched,
    int? BufferLimit);

public enum EndpointDirection
{
    Listening,
    Sending
}
