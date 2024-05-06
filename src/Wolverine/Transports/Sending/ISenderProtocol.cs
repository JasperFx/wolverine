namespace Wolverine.Transports.Sending;

public interface ISenderProtocol
{
    Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch);
}

/// <summary>
/// Marker interface that tells Wolverine that the current sender protocol for batching
/// supports native scheduling
/// </summary>
public interface ISenderProtocolWithNativeScheduling : ISenderProtocol;