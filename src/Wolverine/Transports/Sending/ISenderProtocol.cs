namespace Wolverine.Transports.Sending;

public interface ISenderProtocol
{
    Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch);
}