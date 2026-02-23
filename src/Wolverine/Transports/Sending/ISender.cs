namespace Wolverine.Transports.Sending;

public interface ISenderRequiresCallback : IDisposable
{
    void RegisterCallback(ISenderCallback senderCallback);
}

public interface ISender
{
    bool SupportsNativeScheduledSend { get; }
    bool SupportsNativeScheduledCancellation { get; }
    Uri Destination { get; }
    Task<bool> PingAsync();
    ValueTask SendAsync(Envelope envelope);
}

/// <summary>
///     Marker interface for senders that support cancelling a previously scheduled message.
///     Each transport interprets the scheduling token differently (e.g., long for ASB, Guid for DB senders).
/// </summary>
public interface ISenderWithScheduledCancellation : ISender
{
    Task CancelScheduledMessageAsync(object schedulingToken, CancellationToken cancellation = default);
}

