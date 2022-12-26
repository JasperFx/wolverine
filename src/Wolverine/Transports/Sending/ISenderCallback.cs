namespace Wolverine.Transports.Sending;

public interface ISenderCallback
{
    Task MarkSuccessfulAsync(OutgoingMessageBatch outgoing);
    Task MarkTimedOutAsync(OutgoingMessageBatch outgoing);
    Task MarkSerializationFailureAsync(OutgoingMessageBatch outgoing);
    Task MarkQueueDoesNotExistAsync(OutgoingMessageBatch outgoing);
    Task MarkProcessingFailureAsync(OutgoingMessageBatch outgoing);
    Task MarkProcessingFailureAsync(OutgoingMessageBatch outgoing, Exception? exception);
    Task MarkSenderIsLatchedAsync(OutgoingMessageBatch outgoing);
}